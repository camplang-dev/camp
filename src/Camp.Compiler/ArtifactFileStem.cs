using System.IO;
using System.Text;

namespace Camp.Compiler;

public static class ArtifactFileStem
{
	public static string FromSourcePath(string path)
	{
		if (path == "-")
			return "stdin";

		string name = Path.GetFileNameWithoutExtension(path);
		StringBuilder builder = new();
		foreach (char ch in name)
			builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
		return builder.Length == 0 ? "camp" : builder.ToString();
	}
}
