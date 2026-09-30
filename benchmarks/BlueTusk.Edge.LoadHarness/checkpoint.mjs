import { rename, writeFile } from "node:fs/promises";

export async function publishCheckpoint(path, position) {
  // Publish a complete snapshot. The observer may still be reading the previous
  // file, so it must share deletion while this same-directory rename replaces it.
  const pending = `${path}.pending`;
  await writeFile(pending, position);
  for (let attempt = 0; ; attempt++) {
    try { await rename(pending, path); return; }
    catch (error) {
      // Windows can refuse replacement until an existing snapshot handle closes,
      // even when it shares deletion. Retry only those lock errors for at most
      // 235 ms; preserve and surface any persistent failure.
      if (process.platform !== "win32" || !["EBUSY", "EPERM"].includes(error.code) || attempt >= 8) throw error;
      await new Promise(resolve => setTimeout(resolve, Math.min(5 * 2 ** attempt, 40)));
    }
  }
}
