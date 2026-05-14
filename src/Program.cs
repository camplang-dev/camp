using System.Collections;
using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Camp.Compiler;

Argument<string> fileArgument = new("filename")
{
	Description = "The source file to read, or '-' to read from standard input."
};

Option<bool> tokensOption = new("--tokens")
{
	Description = "Print one token per line."
};

Option<bool> syntaxOption = new("--syntax")
{
	Description = "Parse and print the syntax tree as XML."
};

RootCommand rootCommand = new("Camp compiler");
rootCommand.Arguments.Add(fileArgument);
rootCommand.Options.Add(tokensOption);
rootCommand.Options.Add(syntaxOption);
rootCommand.SetAction(parseResult =>
{
	string? filename = parseResult.GetValue(fileArgument);
	bool printTokens = parseResult.GetValue(tokensOption);
	bool printSyntax = parseResult.GetValue(syntaxOption);

	return Run(filename, printTokens, printSyntax);
});

return rootCommand.Parse(args).Invoke();

static int Run(string? filename, bool printTokens, bool printSyntax)
{
	if (string.IsNullOrWhiteSpace(filename))
	{
		Console.Error.WriteLine("A filename is required.");
		return 1;
	}

	if (printTokens && printSyntax)
	{
		Console.Error.WriteLine("Specify only one output mode: --tokens or --syntax.");
		return 1;
	}

	if (!TryReadInput(filename, out string text))
		return 1;

	TokenSequence tokens = new(CampTokenizer.Tokenize(text));

	if (printTokens)
	{
		PrintTokenLines(tokens);
		return 0;
	}

	if (printSyntax)
		return PrintSyntaxXml(filename, tokens);

	PrintColoredSource(tokens);
	return 0;
}

static bool TryReadInput(string filename, out string text)
{
	if (filename == "-")
	{
		text = Console.In.ReadToEnd();
		return true;
	}

	try
	{
		text = File.ReadAllText(filename);
		return true;
	}
	catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
	{
		Console.Error.WriteLine($"{filename}: {ex.Message}");
		text = "";
		return false;
	}
}

static int PrintSyntaxXml(string filename, TokenSequence tokens)
{
	CompilationUnitSyntax syntax = CampParser.Parse(tokens, out IReadOnlyList<ParseDiagnostic> diagnostics);

	if (diagnostics.Count > 0)
	{
		foreach (ParseDiagnostic diagnostic in diagnostics)
			PrintDiagnostic(filename, diagnostic);

		return 1;
	}

	XDocument document = new(new XDeclaration("1.0", "utf-8", null), SerializeSyntax(syntax));
	XmlWriterSettings settings = new()
	{
		Indent = true,
		OmitXmlDeclaration = false
	};

	using XmlWriter writer = XmlWriter.Create(Console.Out, settings);
	document.Save(writer);
	return 0;
}

static void PrintDiagnostic(string filename, ParseDiagnostic diagnostic)
{
	if (diagnostic.Range is TokenRange range)
		Console.Error.WriteLine($"{filename}({range.StartLineNumber},{range.StartColumn}): error: {diagnostic.Message}");
	else
		Console.Error.WriteLine($"{filename}: error: {diagnostic.Message}");
}

static void PrintColoredSource(IEnumerable<Token> tokens)
{
	foreach (Token token in tokens)
		WriteColored(token.Value, GetTokenColor(token.Class));

	ResetColor();
}

static void PrintTokenLines(IEnumerable<Token> tokens)
{
	foreach (Token token in tokens)
	{
		WriteSubtle("\"");
		WriteEscapedTokenValue(token.Value);
		WriteSubtle("\" ");
		WriteColored(token.Class.ToString(), GetTokenColor(token.Class));
		Console.Out.WriteLine();
	}

	ResetColor();
}

static void WriteEscapedTokenValue(string value)
{
	foreach (char c in value)
	{
		switch (c)
		{
			case '\t':
				WriteColored("\\t", ConsoleColor.Blue);
				break;

			case '\r':
				WriteColored("\\r", ConsoleColor.Blue);
				break;

			case '\n':
				WriteColored("\\n", ConsoleColor.Blue);
				break;

			default:
				WriteColored(c.ToString(), ConsoleColor.Magenta);
				break;
		}
	}
}

static ConsoleColor GetTokenColor(TokenClass tokenClass)
{
	return tokenClass switch
	{
		TokenClass.Identifier => ConsoleColor.Blue,
		TokenClass.AttributeIdentifier => ConsoleColor.Green,
		TokenClass.Number => ConsoleColor.Magenta,
		TokenClass.String => ConsoleColor.Red,
		TokenClass.Symbol => ConsoleColor.DarkGray,
		TokenClass.Preprocessor => ConsoleColor.Magenta,
		TokenClass.LineComment or TokenClass.BlockComment => ConsoleColor.Green,
		TokenClass.Whitespace or TokenClass.NewLine => ConsoleColor.Gray,
		TokenClass.Invalid => ConsoleColor.Red,
		_ => ConsoleColor.Gray
	};
}

static void WriteSubtle(string value)
{
	WriteColored(value, ConsoleColor.Gray);
}

static void WriteColored(string value, ConsoleColor color)
{
	if (!Console.IsOutputRedirected)
		Console.ForegroundColor = color;

	Console.Out.Write(value);
}

static void ResetColor()
{
	if (!Console.IsOutputRedirected)
		Console.ResetColor();
}

static XElement SerializeSyntax(SyntaxNode syntax, string? elementName = null)
{
	Type type = syntax.GetType();
	string typeName = GetXmlName(type.Name);
	XElement element = new(elementName ?? typeName);

	if (elementName is not null && elementName != typeName)
		element.SetAttributeValue("Type", typeName);

	foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
	{
		object? value = property.GetValue(syntax);
		if (value is null)
			continue;

		if (IsTokenType(property.PropertyType) && value is Token token)
		{
			element.SetAttributeValue(property.Name, token.Value);
		}
		else if (IsTokenRangeType(property.PropertyType) && value is TokenRange range)
		{
			element.SetAttributeValue(property.Name, range.Value);
		}
		else if (IsListType(property.PropertyType) && value is IEnumerable items)
		{
			element.Add(SerializeList(property.Name, items));
		}
		else if (value is SyntaxNode childSyntax)
		{
			element.Add(SerializeSyntax(childSyntax, property.Name));
		}
		else
		{
			element.SetAttributeValue(property.Name, Convert.ToString(value, CultureInfo.InvariantCulture));
		}
	}

	return element;
}

static XElement SerializeList(string name, IEnumerable items)
{
	XElement element = new(name);

	foreach (object? item in items)
	{
		if (item is null)
			continue;

		switch (item)
		{
			case SyntaxNode syntax:
				element.Add(SerializeSyntax(syntax));
				break;

			case Token token:
				element.Add(new XElement("Token", new XAttribute("Value", token.Value)));
				break;

			case TokenRange range:
				element.Add(new XElement("TokenRange", new XAttribute("Value", range.Value)));
				break;

			default:
				element.Add(new XElement("Value", Convert.ToString(item, CultureInfo.InvariantCulture)));
				break;
		}
	}

	return element;
}

static bool IsListType(Type type)
{
	return type != typeof(string)
		&& type != typeof(Token)
		&& type != typeof(TokenRange)
		&& type != typeof(Token?)
		&& type != typeof(TokenRange?)
		&& typeof(IEnumerable).IsAssignableFrom(type);
}

static bool IsTokenType(Type type)
{
	return type == typeof(Token) || type == typeof(Token?);
}

static bool IsTokenRangeType(Type type)
{
	return type == typeof(TokenRange) || type == typeof(TokenRange?);
}

static string GetXmlName(string typeName)
{
	return typeName.EndsWith("Syntax", StringComparison.Ordinal)
		? typeName[..^"Syntax".Length]
		: typeName;
}
