import { readSync } from "node:fs";

const maximumDocumentBytes = 64 * 1024;

// Read at most one byte beyond the limit, even if the file grows after open.
export function readDeploymentDocument(fd: number): Buffer | undefined {
  const bytes = Buffer.allocUnsafe(maximumDocumentBytes + 1);
  let length = 0;
  while (length < bytes.length) {
    const count = readSync(fd, bytes, length, bytes.length - length, null);
    if (count === 0) return bytes.subarray(0, length);
    length += count;
  }
  return undefined;
}
