#!/usr/bin/env node
// Generates ready-to-submit Homebrew, Scoop and Winget package-manager manifests
// from a release version and verified standalone archive SHA-256 checksums.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const SHA256_RE = /^[0-9a-f]{64}$/;
const VERSION_RE = /^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$/;

export function buildManifests({ version, repo = 'jkelly/PiSharp', checksums }) {
	if (!VERSION_RE.test(version)) throw new Error(`Invalid release version: ${version}`);
	const required = ['win-x64', 'linux-x64', 'osx-arm64'];
	for (const rid of required) {
		const hash = checksums?.[rid];
		if (typeof hash !== 'string' || !SHA256_RE.test(hash)) {
			throw new Error(`Missing or invalid 64-char lowercase sha256 for ${rid}.`);
		}
	}

	const base = `https://github.com/${repo}/releases/download/v${version}`;
	const winUrl = `${base}/pisharp-${version}-win-x64.zip`;
	const linuxUrl = `${base}/pisharp-${version}-linux-x64.zip`;
	const macUrl = `${base}/pisharp-${version}-osx-arm64.zip`;

	const homebrew = [
		'class Pisharp < Formula',
		'  desc "Independent native .NET 10 port of the Pi coding agent"',
		'  homepage "https://pisharp.ai"',
		`  version "${version}"`,
		'  license "MIT"',
		'',
		'  on_macos do',
		'    on_arm do',
		`      url "${macUrl}"`,
		`      sha256 "${checksums['osx-arm64']}"`,
		'    end',
		'  end',
		'',
		'  on_linux do',
		'    on_intel do',
		`      url "${linuxUrl}"`,
		`      sha256 "${checksums['linux-x64']}"`,
		'    end',
		'  end',
		'',
		'  def install',
		'    libexec.install Dir["*"]',
		'    chmod 0755, libexec/"PiSharp.Cli"',
		'    bin.install_symlink libexec/"PiSharp.Cli" => "pisharp"',
		'  end',
		'',
		'  test do',
		`    assert_match "${version}", shell_output("#{bin}/pisharp --version")`,
		'  end',
		'end',
		'',
	].join('\n');

	const scoop =
		JSON.stringify(
			{
				version,
				description: 'Independent native .NET 10 port of the Pi coding agent',
				homepage: 'https://pisharp.ai',
				license: 'MIT',
				architecture: {
					'64bit': {
						url: winUrl,
						hash: checksums['win-x64'],
					},
				},
				bin: [['PiSharp.Cli.exe', 'pisharp']],
				checkver: {
					github: `https://github.com/${repo}`,
				},
				autoupdate: {
					architecture: {
						'64bit': {
							url: `https://github.com/${repo}/releases/download/v$version/pisharp-$version-win-x64.zip`,
						},
					},
				},
			},
			null,
			2,
		) + '\n';

	const wingetVersion = [
		`PackageIdentifier: PiSharp.Cli`,
		`PackageVersion: ${version}`,
		`DefaultLocale: en-US`,
		`ManifestType: version`,
		`ManifestVersion: 1.9.0`,
		'',
	].join('\n');

	const wingetInstaller = [
		`PackageIdentifier: PiSharp.Cli`,
		`PackageVersion: ${version}`,
		`InstallerType: zip`,
		`NestedInstallerType: portable`,
		`NestedInstallerFiles:`,
		`  - RelativeFilePath: PiSharp.Cli.exe`,
		`    PortableCommandAlias: pisharp`,
		`Installers:`,
		`  - Architecture: x64`,
		`    InstallerUrl: ${winUrl}`,
		`    InstallerSha256: ${checksums['win-x64'].toUpperCase()}`,
		`ManifestType: installer`,
		`ManifestVersion: 1.9.0`,
		'',
	].join('\n');

	const wingetLocale = [
		`PackageIdentifier: PiSharp.Cli`,
		`PackageVersion: ${version}`,
		`PackageLocale: en-US`,
		`Publisher: PiSharp contributors`,
		`PublisherUrl: https://pisharp.ai`,
		`PackageName: PiSharp`,
		`PackageUrl: https://pisharp.ai`,
		`License: MIT`,
		`LicenseUrl: https://github.com/${repo}/blob/v${version}/LICENSE`,
		`ShortDescription: Independent native .NET 10 port of the Pi coding agent.`,
		`ReleaseNotesUrl: https://github.com/${repo}/releases/tag/v${version}`,
		`ManifestType: defaultLocale`,
		`ManifestVersion: 1.9.0`,
		'',
	].join('\n');

	return {
		'homebrew/Formula/pisharp.rb': homebrew,
		'scoop/pisharp.json': scoop,
		'winget/PiSharp.Cli.yaml': wingetVersion,
		'winget/PiSharp.Cli.installer.yaml': wingetInstaller,
		'winget/PiSharp.Cli.locale.en-US.yaml': wingetLocale,
	};
}

export function parseChecksumsFile(content, version) {
	const out = {};
	for (const rawLine of content.split(/\r?\n/)) {
		const line = rawLine.trim();
		if (!line) continue;
		const match = line.match(/^([0-9a-f]{64})\s+pisharp-(.+)-(win-x64|linux-x64|osx-arm64)\.zip$/i);
		if (!match) continue;
		if (version && match[2] !== version) continue;
		out[match[3].toLowerCase()] = match[1].toLowerCase();
	}
	return out;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
	const args = process.argv.slice(2);
	const getArg = (flag) => {
		const idx = args.indexOf(flag);
		return idx >= 0 ? args[idx + 1] : undefined;
	};
	const version = getArg('--version');
	const checksumsFile = getArg('--checksums');
	const outDir = getArg('--out');
	const repo = getArg('--repo') ?? 'jkelly/PiSharp';
	if (!version || !checksumsFile || !outDir) {
		console.error('Usage: node generate-package-manifests.mjs --version <ver> --checksums <checksums.sha256> --out <dir> [--repo owner/name]');
		process.exit(1);
	}
	const checksums = parseChecksumsFile(readFileSync(checksumsFile, 'utf8'), version);
	const manifests = buildManifests({ version, repo, checksums });
	for (const [relPath, text] of Object.entries(manifests)) {
		const full = join(outDir, relPath);
		mkdirSync(resolve(full, '..'), { recursive: true });
		writeFileSync(full, text, 'utf8');
	}
	console.log(`Generated ${Object.keys(manifests).length} package-manager manifests under ${outDir}`);
}
