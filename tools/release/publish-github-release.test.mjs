import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { publishGitHubRelease } from './publish-github-release.mjs';

const sha = 'a'.repeat(40);
const tagObjectSha = 'b'.repeat(40);
const response = (status, body, code = status === 200 ? 0 : 1) => ({ code, stderr: '',
  stdout: `HTTP/2.0 ${status} status\r\nContent-Type: application/json\r\n\r\n${JSON.stringify(body)}\n` });
const absent = () => response(404, { message: 'Not Found' });

async function fixture(run, { version = '1.5.0.1', notes = true } = {}) {
  const cwd = await mkdtemp(join(tmpdir(), 'pisharp release '));
  try {
    await mkdir(join(cwd, 'packages'));
    await mkdir(join(cwd, 'notes'));
    await writeFile(join(cwd, 'packages', `PiSharp.AI.${version}.nupkg`), 'validated package fixture A');
    await writeFile(join(cwd, 'packages', `PiSharp.Cli.${version}.nupkg`), 'validated package fixture B');
    if (notes) await writeFile(join(cwd, 'notes', `${version}.md`), 'Prepared release notes');
    await run({ cwd, version, tag: `v${version}`, repo: 'jkelly/PiSharp', expectedSourceSha: sha,
      eventName: 'push', refType: 'tag', packageDirectory: 'packages', notesDirectory: 'notes' });
  } finally { await rm(cwd, { recursive: true, force: true }); }
}

function fakeGh(options, { release = null, reference, releaseResponse, uploadCode = 0 } = {}) {
  const commands = [];
  let refCalls = 0, releaseCalls = 0;
  const gh = async (args, context) => {
    commands.push([...args]);
    assert.equal(context.cwd, options.cwd);
    if (args[0] === 'api') {
      assert.deepEqual(args.slice(0, 4), ['api', '--include', '--method', 'GET']);
      const endpoint = args[4];
      if (endpoint === `repos/${options.repo}/git/ref/tags/${options.tag}`) {
        refCalls++;
        return reference ? reference(refCalls) : response(200, { ref: `refs/tags/${options.tag}`, object: { type: 'commit', sha } });
      }
      if (endpoint === `repos/${options.repo}/git/tags/${tagObjectSha}`)
        return response(200, { object: { type: 'commit', sha } });
      if (endpoint === `repos/${options.repo}/releases/tags/${options.tag}`) {
        releaseCalls++;
        return releaseResponse ? releaseResponse(releaseCalls) : release ? response(200, release) : absent();
      }
      throw new Error(`Unexpected API request: ${endpoint}`);
    }
    assert.equal(args[0], 'release');
    assert.ok(['create', 'upload', 'edit'].includes(args[1]), 'no tag creation or unrelated mutation');
    return { code: args[1] === 'upload' ? uploadCode : 0, stdout: '', stderr: '' };
  };
  return { gh, commands, writes: () => commands.filter(args => args[0] === 'release') };
}

test('new release requires the existing exact tag and creates with verified tag, notes and exact asset argv', async () => fixture(async options => {
  const fake = fakeGh(options);
  const result = await publishGitHubRelease(options, fake);
  assert.equal(result.action, 'created');
  assert.equal(result.assets, 2);
  assert.deepEqual(fake.writes(), [[
    'release', 'create', options.tag, '--repo', options.repo, '--verify-tag', '--title', 'PiSharp 1.5.0.1',
    '--notes-file', join(options.cwd, 'notes', '1.5.0.1.md'),
    join(options.cwd, 'packages', 'PiSharp.AI.1.5.0.1.nupkg'), join(options.cwd, 'packages', 'PiSharp.Cli.1.5.0.1.nupkg'),
  ]]);
  assert.deepEqual(fake.commands.map(args => args[0] === 'api' ? args[4].split('/').at(-2) : args[1]), ['tags', 'tags', 'tags', 'create']);
}));

test('new prerelease uses generated notes when no notes file exists', async () => fixture(async options => {
  const fake = fakeGh(options);
  await publishGitHubRelease(options, fake);
  const create = fake.writes()[0];
  assert.ok(create.includes('--generate-notes'));
  assert.ok(create.includes('--prerelease'));
  assert.ok(!create.includes('--notes-file'));
}, { version: '1.5.0.1-rc.1', notes: false }));

test('matching draft uploads every validated asset before rechecking identity and promoting', async () => fixture(async options => {
  const fake = fakeGh(options, { release: { id: 23, tag_name: options.tag, draft: true } });
  const result = await publishGitHubRelease(options, fake);
  assert.equal(result.action, 'promoted');
  assert.deepEqual(fake.commands.map(args => args[0] === 'api' ? 'GET' : args[1]), ['GET', 'GET', 'upload', 'GET', 'GET', 'edit']);
  const [upload, edit] = fake.writes();
  assert.ok(upload.includes('--clobber'));
  assert.deepEqual(upload.slice(-2), [join(options.cwd, 'packages', 'PiSharp.AI.1.5.0.1.nupkg'), join(options.cwd, 'packages', 'PiSharp.Cli.1.5.0.1.nupkg')]);
  assert.ok(edit.includes('--draft=false'));
  assert.ok(edit.includes('--prerelease=false'));
  assert.ok(edit.includes('--notes-file'));
}));

test('draft prerelease promotion preserves prepared draft notes when no file is supplied', async () => fixture(async options => {
  const fake = fakeGh(options, { release: { id: 23, tag_name: options.tag, draft: true, body: 'Prepared draft notes' } });
  await publishGitHubRelease(options, fake);
  const edit = fake.writes().at(-1);
  assert.ok(edit.includes('--prerelease=true'));
  assert.ok(!edit.includes('--notes-file') && !edit.includes('--generate-notes'));
}, { version: '1.5.0.1-rc.1', notes: false }));

test('published release refuses without uploads, edits or creation', async () => fixture(async options => {
  const fake = fakeGh(options, { release: { id: 23, tag_name: options.tag, draft: false } });
  await assert.rejects(publishGitHubRelease(options, fake), /already published/);
  assert.deepEqual(fake.writes(), []);
}));

test('missing or wrong-source tag refuses before any release mutation', async () => {
  for (const reference of [() => absent(), () => response(200, { ref: 'refs/tags/v1.5.0.1', object: { type: 'commit', sha: 'c'.repeat(40) } })]) {
    await fixture(async options => {
      const fake = fakeGh(options, { reference });
      await assert.rejects(publishGitHubRelease(options, fake));
      assert.deepEqual(fake.writes(), []);
    });
  }
});

test('annotated tag must peel to the exact source commit', async () => fixture(async options => {
  const fake = fakeGh(options, { reference: () => response(200, { ref: `refs/tags/${options.tag}`, object: { type: 'tag', sha: tagObjectSha } }) });
  await publishGitHubRelease(options, fake);
  assert.equal(fake.commands.filter(args => args[4]?.endsWith(`/git/tags/${tagObjectSha}`)).length, 2);
  assert.equal(fake.writes()[0][1], 'create');
}));

test('auth, network, malformed and nonstandard 404 gh failures are never absence', async () => {
  for (const result of [response(401, { message: 'Bad credentials' }), response(403, { message: 'API rate limit exceeded' }),
    { code: 1, stdout: '', stderr: 'network failure' }, { code: 1, stdout: '{"message":"Not Found"}', stderr: 'network failure' },
    response(404, { message: 'Not Found' }, 2), { code: 0, stdout: 'HTTP/2.0 200 OK\n\ninvalid-json', stderr: '' }]) {
    await fixture(async options => {
      const fake = fakeGh(options, { releaseResponse: () => result });
      await assert.rejects(publishGitHubRelease(options, fake));
      assert.deepEqual(fake.writes(), []);
    });
  }
});

test('failed draft upload never promotes', async () => fixture(async options => {
  const fake = fakeGh(options, { release: { id: 23, tag_name: options.tag, draft: true }, uploadCode: 1 });
  await assert.rejects(publishGitHubRelease(options, fake), /upload failed/);
  assert.deepEqual(fake.writes().map(args => args[1]), ['upload']);
}));

test('moved tag or changed draft after upload refuses promotion', async () => {
  for (const change of ['tag', 'published', 'replaced']) {
    await fixture(async options => {
      const fake = fakeGh(options, {
        reference: count => response(200, { ref: `refs/tags/${options.tag}`, object: { type: 'commit', sha: change === 'tag' && count > 1 ? 'c'.repeat(40) : sha } }),
        releaseResponse: count => response(200, { id: change === 'replaced' && count > 1 ? 24 : 23, tag_name: options.tag, draft: !(change === 'published' && count > 1) }),
      });
      await assert.rejects(publishGitHubRelease(options, fake));
      assert.deepEqual(fake.writes().map(args => args[1]), ['upload']);
    });
  }
});

test('manual run, branch push, tag/version mismatch and invalid source SHA refuse before gh', async () => {
  for (const changed of [{ eventName: 'workflow_dispatch' }, { refType: 'branch' }, { tag: 'v1.5.0' }, { expectedSourceSha: 'main' }]) {
    await fixture(async options => {
      const fake = fakeGh(options);
      await assert.rejects(publishGitHubRelease({ ...options, ...changed }, fake));
      assert.deepEqual(fake.commands, []);
    });
  }
});

test('wrong-version package refuses before gh', async () => fixture(async options => {
  await writeFile(join(options.cwd, 'packages', 'PiSharp.Bad.1.5.0.nupkg'), 'wrong version');
  const fake = fakeGh(options);
  await assert.rejects(publishGitHubRelease(options, fake), /exact version/);
  assert.deepEqual(fake.commands, []);
}));

test('empty package or empty package directory refuses before gh', async () => {
  for (const emptyDirectory of [false, true]) {
    await fixture(async options => {
      if (emptyDirectory) {
        await rm(join(options.cwd, 'packages'), { recursive: true });
        await mkdir(join(options.cwd, 'packages'));
      } else await writeFile(join(options.cwd, 'packages', 'PiSharp.AI.1.5.0.1.nupkg'), '');
      const fake = fakeGh(options);
      await assert.rejects(publishGitHubRelease(options, fake));
      assert.deepEqual(fake.commands, []);
    });
  }
});

test('mismatched draft tag refuses without mutation', async () => fixture(async options => {
  const fake = fakeGh(options, { release: { id: 23, tag_name: 'v1.5.0', draft: true } });
  await assert.rejects(publishGitHubRelease(options, fake), /mismatched release/);
  assert.deepEqual(fake.writes(), []);
}));
