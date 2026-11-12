/** The address that downloads an attachment; the server answers with a download whatever the file is. */
export function attachmentHref(messageId: string, partId: string): string {
  return `/api/v1/messages/${encodeURIComponent(messageId)}/attachments/${encodeURIComponent(partId)}`;
}

/** The short label for the file-type tile: the extension in capitals ("PDF"), or nothing if the name has no usable one. */
export function extensionOf(fileName: string): string | null {
  const dot = fileName.lastIndexOf('.');
  const extension = dot > 0 ? fileName.slice(dot + 1) : '';
  return /^[\p{L}\p{N}]{1,4}$/u.test(extension) ? extension.toLocaleUpperCase() : null;
}
