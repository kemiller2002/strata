import crypto from "node:crypto";
import fs from "node:fs";
import fsPromises from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { pipeline } from "node:stream/promises";
import { Readable } from "node:stream";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

const RID_BY_PLATFORM_ARCH = {
  "linux-x64": "linux-x64",
  "linux-arm64": "linux-arm64",
  "darwin-x64": "osx-x64",
  "darwin-arm64": "osx-arm64",
  "win32-x64": "win-x64"
};

function rosVersion() {
  const manifestPath = path.join(projectRoot, ".echelon", "ros.json");
  if (fs.existsSync(manifestPath)) {
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    if (typeof manifest.installedVersion !== "string" || manifest.installedVersion.trim().length === 0) {
      throw new Error(".echelon/ros.json does not contain a usable installedVersion");
    }
    return manifest.installedVersion;
  }

  const configuration = JSON.parse(fs.readFileSync(path.join(projectRoot, "ros.json"), "utf8"));
  if (typeof configuration.rosVersion !== "string" || configuration.rosVersion.trim().length === 0) {
    throw new Error("ros.json does not contain a usable rosVersion");
  }
  return configuration.rosVersion;
}

function isStableVersion(version) {
  return /^\d+\.\d+\.\d+$/.test(version);
}

function resolveRid({ platform = process.platform, arch = process.arch } = {}) {
  return RID_BY_PLATFORM_ARCH[`${platform}-${arch}`] ?? null;
}

function binaryName(rid) {
  return rid.startsWith("win-") ? "ros-fs.exe" : "ros-fs";
}

function releaseAssetName(rid) {
  return rid.startsWith("win-") ? `ros-fs-${rid}.exe` : `ros-fs-${rid}`;
}

function releaseBaseUrl(version) {
  const override = process.env.ROS_FS_RELEASE_BASE_URL;
  if (override) return override.replace(/\/+$/, "");
  return `https://github.com/kemiller2002/repository-operating-system/releases/download/v${version}`;
}

function cacheDirectory(version, rid) {
  const override = process.env.ROS_FS_CACHE_DIR;
  const base = override ? path.resolve(override) : path.join(os.homedir(), ".cache", "ros-fs");
  return path.join(base, version, rid);
}

async function fetchText(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`request to ${url} failed: ${response.status} ${response.statusText}`);
  }
  return response.text();
}

async function fetchToFile(url, destination) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`request to ${url} failed: ${response.status} ${response.statusText}`);
  }
  await fsPromises.mkdir(path.dirname(destination), { recursive: true });
  await pipeline(Readable.fromWeb(response.body), fs.createWriteStream(destination));
}

function sha256File(filePath) {
  return new Promise((resolve, reject) => {
    const hash = crypto.createHash("sha256");
    const stream = fs.createReadStream(filePath);
    stream.on("data", (chunk) => hash.update(chunk));
    stream.on("end", () => resolve(hash.digest("hex")));
    stream.on("error", reject);
  });
}

function parseChecksums(text) {
  const entries = new Map();
  for (const line of text.split("\n")) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    const match = trimmed.match(/^([0-9a-fA-F]{64})\s+\*?(.+)$/);
    if (!match) continue;
    entries.set(match[2].trim(), match[1].toLowerCase());
  }
  return entries;
}

async function ensureBinary({ version, rid, log = () => {} }) {
  const directory = cacheDirectory(version, rid);
  const finalPath = path.join(directory, binaryName(rid));
  if (fs.existsSync(finalPath)) {
    return finalPath;
  }

  const assetName = releaseAssetName(rid);
  const base = releaseBaseUrl(version);
  log(`ros binary for ${rid} not cached; downloading from ${base}/${assetName}`);

  const checksumsText = await fetchText(`${base}/checksums.txt`);
  const checksums = parseChecksums(checksumsText);
  const expected = checksums.get(assetName);
  if (!expected) {
    throw new Error(`checksums.txt at ${base} has no entry for ${assetName}`);
  }

  const temporaryPath = `${finalPath}.download-${process.pid}`;
  await fetchToFile(`${base}/${assetName}`, temporaryPath);

  const actual = await sha256File(temporaryPath);
  if (actual !== expected) {
    await fsPromises.rm(temporaryPath, { force: true });
    throw new Error(
      `checksum mismatch for ${assetName}: expected ${expected}, got ${actual}. The download may be corrupt or tampered with; not executing it.`
    );
  }

  if (!rid.startsWith("win-")) {
    await fsPromises.chmod(temporaryPath, 0o755);
  }
  await fsPromises.rename(temporaryPath, finalPath);
  return finalPath;
}

export function unsupportedPlatformMessage({ platform = process.platform, arch = process.arch } = {}) {
  return (
    `./ros has no prebuilt binary for ${platform}/${arch}. ` +
    "Supported platforms: linux/x64, linux/arm64, darwin/x64, darwin/arm64, win32/x64."
  );
}

export function nonStableVersionMessage(version) {
  return (
    `./ros: version ${version} is a main-branch snapshot, not a stable release -- ` +
    "no GitHub Release (and therefore no ros-fs binary) is ever published for a snapshot " +
    "version. Bootstrap with a stable published version instead (see PACKAGE-USAGE.md's " +
    '"Install from npm" section), or wait for the next stable release.'
  );
}

export async function run(argv, { log = (message) => process.stderr.write(`${message}\n`) } = {}) {
  const rid = resolveRid();
  if (!rid) {
    log(unsupportedPlatformMessage());
    return 1;
  }

  let version;
  try {
    version = rosVersion();
  } catch (error) {
    log(`./ros: cannot determine the installed ROS version: ${error.message}`);
    return 1;
  }

  if (!isStableVersion(version)) {
    log(nonStableVersionMessage(version));
    return 1;
  }

  let binaryPath;
  try {
    binaryPath = await ensureBinary({ version, rid, log });
  } catch (error) {
    log(`./ros: failed to obtain the ${rid} binary for version ${version}: ${error.message}`);
    log(
      "This launcher needs network access on first use of a given version/platform pair; " +
        "once downloaded, the binary is cached and later runs work offline."
    );
    return 1;
  }

  const result = spawnSync(binaryPath, argv, { stdio: "inherit" });
  if (result.error) {
    log(`./ros: failed to execute cached binary at ${binaryPath}: ${result.error.message}`);
    return 1;
  }
  return result.status ?? 1;
}

export const internal = { resolveRid, binaryName, releaseAssetName, releaseBaseUrl, cacheDirectory, parseChecksums, ensureBinary, rosVersion, isStableVersion };
