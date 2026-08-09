using System;
using System.IO;
using System.Text;

namespace Camp.Compiler;

public enum BuildFileWriteStatus
{
	Changed,
	Unchanged
}

public static class BuildFileIO
{
	public static BuildFileWriteStatus WriteTextIfChanged(string path, string content, Encoding encoding)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
		byte[] bytes = encoding.GetBytes(content);
		if (File.Exists(path))
		{
			byte[] existing = File.ReadAllBytes(path);
			if (existing.AsSpan().SequenceEqual(bytes))
				return BuildFileWriteStatus.Unchanged;
		}
		File.WriteAllBytes(path, bytes);
		return BuildFileWriteStatus.Changed;
	}

	public static BuildFileWriteStatus CopyIfChanged(string source, string destination)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
		if (File.Exists(destination))
		{
			byte[] sourceBytes = File.ReadAllBytes(source);
			byte[] destinationBytes = File.ReadAllBytes(destination);
			if (sourceBytes.AsSpan().SequenceEqual(destinationBytes))
				return BuildFileWriteStatus.Unchanged;
			File.WriteAllBytes(destination, sourceBytes);
			return BuildFileWriteStatus.Changed;
		}
		File.Copy(source, destination, overwrite: false);
		return BuildFileWriteStatus.Changed;
	}
}
