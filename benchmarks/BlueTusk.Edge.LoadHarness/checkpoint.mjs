import { rename, writeFile } from "node:fs/promises";

export async function publishCheckpoint(path, position) {
  // Publish a complete snapshot. The observer may still be reading the previous
  // file, so it must share deletion while this same-directory rename replaces it.
  const pending = `${path}.pending`;
  await writeFile(pending, position);
  const retryDeadline = performance.now() + 3000;
  let retryDelay = 5;
  for (;;) {
    try { await rename(pending, path); return; }
    catch (error) {
      // Windows can refuse replacement while the observer reads its open snapshot.
      // Allow that reader to finish and surface any persistent rename failure.
      const remaining = retryDeadline - performance.now();
      if (process.platform !== "win32" || !["EBUSY", "EPERM"].includes(error.code) || remaining <= 0) throw error;
      await new Promise(resolve => setTimeout(resolve, Math.min(retryDelay, remaining)));
      retryDelay = Math.min(retryDelay * 2, 40);
    }
  }
}
