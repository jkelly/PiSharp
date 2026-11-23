import test from 'node:test';
import assert from 'node:assert/strict';
import { buildManifests, parseChecksumsFile } from './generate-package-manifests.mjs';

const sampleChecksums = {
	'win-x64': 'a'.repeat(64),
	'linux-x64': 'b'.repeat(64),
	'osx-arm64': 'c'.repeat(64),
};

test('parseChecksumsFile extracts verified standalone hashes by RID', () => {
	const text = [
		`${'a'.repeat(64)}  pisharp-1.6.0.1-win-x64.zip`,
		`${'b'.repeat(64)}  pisharp-1.6.0.1-linux-x64.zip`,
		`${'c'.repeat(64)}  pisharp-1.6.0.1-osx-arm64.zip`,
	].join('\n');
	assert.deepEqual(parseChecksumsFile(text, '1.6.0.1'), sampleChecksums);
});

test('buildManifests renders Homebrew, Scoop and Winget manifests cleanly', () => {
	const files = buildManifests({ version: '1.6.0.1', checksums: sampleChecksums });
	assert.equal(Object.keys(files).length, 5);
	assert.match(files['homebrew/Formula/pisharp.rb'], /version "1\.6\.0\.1"/);
	assert.match(files['homebrew/Formula/pisharp.rb'], new RegExp(sampleChecksums['osx-arm64']));
	assert.match(files['homebrew/Formula/pisharp.rb'], new RegExp(sampleChecksums['linux-x64']));

	const scoop = JSON.parse(files['scoop/pisharp.json']);
	assert.equal(scoop.version, '1.6.0.1');
	assert.equal(scoop.architecture['64bit'].hash, sampleChecksums['win-x64']);

	assert.match(files['winget/PiSharp.Cli.installer.yaml'], new RegExp(sampleChecksums['win-x64'].toUpperCase()));
	assert.match(files['winget/PiSharp.Cli.yaml'], /PackageVersion: 1\.6\.0\.1/);
	assert.match(files['winget/PiSharp.Cli.locale.en-US.yaml'], /releases\/tag\/v1\.6\.0\.1/);
});

test('buildManifests rejects missing or malformed sha256 or version', () => {
	assert.throws(() => buildManifests({ version: 'bad', checksums: sampleChecksums }), /Invalid release version/);
	assert.throws(() => buildManifests({ version: '1.6.0.1', checksums: { 'win-x64': 'short' } }), /Missing or invalid/);
});
