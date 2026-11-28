import { closeSync, constants, fstatSync, openSync, readSync } from "node:fs";

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

export function readRegularDeploymentDocument(
  path: string,
): Buffer | undefined {
  const fd = openSync(path, constants.O_RDONLY | constants.O_NONBLOCK);
  try {
    if (!fstatSync(fd).isFile()) {
      throw new Error("Deployment input must be a regular file.");
    }
    return readDeploymentDocument(fd);
  } finally {
    closeSync(fd);
  }
}
