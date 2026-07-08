namespace Slate.Server.Attachments;

public record AttachmentDto(string Path, long SizeBytes, string Mime);
