using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Camp.Compiler;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Server;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

CampLspWorkspace workspace = new();
LanguageServer server = await LanguageServer.From(options => options
	.WithInput(Console.OpenStandardInput())
	.WithOutput(Console.OpenStandardOutput())
	.AddHandler(new CampTextDocumentSyncHandler(workspace))
	.AddHandler(new CampHoverHandler(workspace))
	.AddHandler(new CampDefinitionHandler(workspace))
	.AddHandler(new CampDocumentSymbolHandler(workspace))
	.AddHandler(new CampWorkspaceSymbolHandler(workspace))
	.OnStarted((languageServer, _) =>
	{
		workspace.SetLanguageServer(languageServer);
		return Task.CompletedTask;
	}));
await server.WaitForExit;

sealed class CampTextDocumentSyncHandler(CampLspWorkspace workspace) : TextDocumentSyncHandlerBase
{
	public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
	{
		return new TextDocumentAttributes(uri, "camp");
	}

	public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
	{
		workspace.OpenOrChange(request.TextDocument.Uri, request.TextDocument.Text, request.TextDocument.Version);
		return Unit.Task;
	}

	public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
	{
		string text = request.ContentChanges.LastOrDefault()?.Text ?? "";
		workspace.OpenOrChange(request.TextDocument.Uri, text, request.TextDocument.Version);
		return Unit.Task;
	}

	public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
	{
		workspace.Reanalyze(request.TextDocument.Uri);
		return Unit.Task;
	}

	public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
	{
		workspace.Close(request.TextDocument.Uri);
		return Unit.Task;
	}

	protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
	{
		return new TextDocumentSyncRegistrationOptions
		{
			DocumentSelector = CampLsp.Protocol.DocumentSelector,
			Change = TextDocumentSyncKind.Full,
			Save = new BooleanOr<SaveOptions>(true)
		};
	}
}

sealed class CampHoverHandler(CampLspWorkspace workspace) : HoverHandlerBase
{
	public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
	{
		CampHover? hover = workspace.GetHover(request.TextDocument.Uri, CampLsp.ToCampPosition(request.Position));
		return Task.FromResult(hover is null
			? null
			: new Hover
			{
				Contents = new MarkedStringsOrMarkupContent(new MarkupContent
				{
					Kind = MarkupKind.Markdown,
					Value = hover.Markdown
				})
			});
	}

	protected override HoverRegistrationOptions CreateRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
	{
		return new HoverRegistrationOptions { DocumentSelector = CampLsp.Protocol.DocumentSelector };
	}
}

#pragma warning disable CS8609
sealed class CampDefinitionHandler(CampLspWorkspace workspace) : DefinitionHandlerBase
{
	public override Task<LocationOrLocationLinks> Handle(DefinitionParams request, CancellationToken cancellationToken)
	{
		CampSymbolLocation? location = workspace.GetDefinition(request.TextDocument.Uri, CampLsp.ToCampPosition(request.Position));
		if (location is null)
			return Task.FromResult(new LocationOrLocationLinks());
		return Task.FromResult(new LocationOrLocationLinks(new LocationOrLocationLink(new Location
		{
			Uri = DocumentUri.FromFileSystemPath(location.Path),
			Range = CampLsp.ToLspRange(location.Range)
		})));
	}

	protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
	{
		return new DefinitionRegistrationOptions { DocumentSelector = CampLsp.Protocol.DocumentSelector };
	}
}
#pragma warning restore CS8609

#pragma warning disable CS8609
sealed class CampDocumentSymbolHandler(CampLspWorkspace workspace) : DocumentSymbolHandlerBase
{
	public override Task<SymbolInformationOrDocumentSymbolContainer> Handle(DocumentSymbolParams request, CancellationToken cancellationToken)
	{
		IReadOnlyList<CampDocumentSymbol> symbols = workspace.GetDocumentSymbols(request.TextDocument.Uri);
		return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer(symbols.Select(symbol => new SymbolInformationOrDocumentSymbol(CampLsp.ToLspDocumentSymbol(symbol)))));
	}

	protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
	{
		return new DocumentSymbolRegistrationOptions { DocumentSelector = CampLsp.Protocol.DocumentSelector };
	}
}
#pragma warning restore CS8609

#pragma warning disable CS8609
sealed class CampWorkspaceSymbolHandler(CampLspWorkspace workspace) : WorkspaceSymbolsHandlerBase
{
	public override Task<Container<WorkspaceSymbol>?> Handle(WorkspaceSymbolParams request, CancellationToken cancellationToken)
	{
		IReadOnlyList<CampWorkspaceSymbol> symbols = workspace.GetWorkspaceSymbols(request.Query ?? "");
		return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(symbols.Select(CampLsp.ToLspWorkspaceSymbol)));
	}

	protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(WorkspaceSymbolCapability capability, ClientCapabilities clientCapabilities)
	{
		return new WorkspaceSymbolRegistrationOptions();
	}
}
#pragma warning restore CS8609

public sealed class CampLspWorkspace
{
	readonly Dictionary<string, OpenDocument> openDocuments = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, CampAnalysisSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
	ILanguageServer? languageServer;

	public void SetLanguageServer(ILanguageServer server)
	{
		languageServer = server;
		foreach (DocumentUri uri in openDocuments.Values.Select(static document => document.Uri))
			PublishDiagnostics(uri);
	}

	public void OpenOrChange(DocumentUri uri, string text, int? version)
	{
		string path = uri.GetFileSystemPath();
		openDocuments[path] = new OpenDocument(uri, path, text, version);
		Reanalyze(uri);
	}

	public void Close(DocumentUri uri)
	{
		string path = uri.GetFileSystemPath();
		openDocuments.Remove(path);
		snapshots.Remove(path);
		PublishDiagnostics(uri, []);
	}

	public void Reanalyze(DocumentUri uri)
	{
		string path = uri.GetFileSystemPath();
		if (!openDocuments.TryGetValue(path, out OpenDocument? document))
			return;
		CampAnalysisSnapshot snapshot = Analyze(document);
		snapshots[path] = snapshot;
		PublishDiagnostics(uri, snapshot.Diagnostics.Where(diagnostic => string.IsNullOrWhiteSpace(diagnostic.Path) || string.Equals(diagnostic.Path, path, StringComparison.OrdinalIgnoreCase)));
	}

	public CampHover? GetHover(DocumentUri uri, CampTextPosition position)
	{
		if (!TryGetSnapshot(uri, out string path, out CampAnalysisSnapshot? snapshot))
			return null;
		return new CampSymbolQueryService(snapshot!).GetHover(path, position);
	}

	public CampSymbolLocation? GetDefinition(DocumentUri uri, CampTextPosition position)
	{
		if (!TryGetSnapshot(uri, out string path, out CampAnalysisSnapshot? snapshot))
			return null;
		return new CampSymbolQueryService(snapshot!).GetDefinition(path, position);
	}

	public IReadOnlyList<CampDocumentSymbol> GetDocumentSymbols(DocumentUri uri)
	{
		if (!TryGetSnapshot(uri, out string path, out CampAnalysisSnapshot? snapshot))
			return [];
		return new CampSymbolQueryService(snapshot!).GetDocumentSymbols(path);
	}

	public IReadOnlyList<CampWorkspaceSymbol> GetWorkspaceSymbols(string query)
	{
		EnsureOpenDocumentSnapshots();
		return snapshots.Values
			.SelectMany(snapshot => new CampSymbolQueryService(snapshot).GetWorkspaceSymbols(query))
			.DistinctBy(static symbol => (symbol.Name, symbol.Kind, symbol.Location.Path, symbol.Location.Range.Start.Line, symbol.Location.Range.Start.Character))
			.OrderBy(static symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(static symbol => symbol.Location.Path, StringComparer.OrdinalIgnoreCase)
			.ThenBy(static symbol => symbol.Location.Range.Start.Line)
			.ThenBy(static symbol => symbol.Location.Range.Start.Character)
			.ToList();
	}

	void EnsureOpenDocumentSnapshots()
	{
		foreach (OpenDocument document in openDocuments.Values)
		{
			if (!snapshots.ContainsKey(document.Path))
				snapshots[document.Path] = Analyze(document);
		}
	}

	bool TryGetSnapshot(DocumentUri uri, out string path, out CampAnalysisSnapshot? snapshot)
	{
		path = uri.GetFileSystemPath();
		if (!snapshots.TryGetValue(path, out snapshot) && openDocuments.TryGetValue(path, out OpenDocument? document))
		{
			snapshot = Analyze(document);
			snapshots[path] = snapshot;
		}
		return snapshot is not null;
	}

	CampAnalysisSnapshot Analyze(OpenDocument document)
	{
		CompilerRequest request = CreateRequest(document.Path);
		return CampLanguageService.Analyze(request, [new CampSourceOverlay(document.Path, document.Text, document.Version ?? 0)]);
	}

	CompilerRequest CreateRequest(string documentPath)
	{
		string? buildFile = CampProjectLoader.FindNearestBuildFile(documentPath);
		if (buildFile is not null)
		{
			string root = Path.GetDirectoryName(buildFile) ?? Directory.GetCurrentDirectory();
			CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(buildFile, CampProjectEnvironment.Create(root), CampProjectCommandKind.LanguageService);
			if (result.Success)
			{
				result.Request.IncludeFiles.AddRange(result.ProjectReferenceApiHeaders);
				return result.Request;
			}
		}

		string workingDirectory = Path.GetDirectoryName(documentPath) ?? Directory.GetCurrentDirectory();
		CompilerRequest request = new()
		{
			RuntimeRoot = AppContext.BaseDirectory,
			WorkingDirectory = workingDirectory
		};
		request.Files.Add(Path.GetFileName(documentPath));
		return request;
	}

	void PublishDiagnostics(DocumentUri uri)
	{
		string path = uri.GetFileSystemPath();
		if (snapshots.TryGetValue(path, out CampAnalysisSnapshot? snapshot))
			PublishDiagnostics(uri, snapshot.Diagnostics.Where(diagnostic => string.IsNullOrWhiteSpace(diagnostic.Path) || string.Equals(diagnostic.Path, path, StringComparison.OrdinalIgnoreCase)));
	}

	void PublishDiagnostics(DocumentUri uri, IEnumerable<CampSourceDiagnostic> diagnostics)
	{
		if (languageServer is null)
			return;
		languageServer.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
		{
			Uri = uri,
			Diagnostics = new Container<Diagnostic>(diagnostics.Select(CampLsp.ToLspDiagnostic))
		});
	}

	sealed record OpenDocument(DocumentUri Uri, string Path, string Text, int? Version);
}

public static class CampLsp
{
	public static CampTextPosition ToCampPosition(Position position)
	{
		return new CampTextPosition(position.Line, position.Character);
	}

	public static LspRange ToLspRange(CampTextRange range)
	{
		return new LspRange(range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);
	}

	public static Diagnostic ToLspDiagnostic(CampSourceDiagnostic diagnostic)
	{
#pragma warning disable CS8625
		return new Diagnostic
		{
			Range = diagnostic.Range is null ? new LspRange(0, 0, 0, 1) : ToLspRange(diagnostic.Range),
			Severity = diagnostic.Severity switch
			{
			Camp.Compiler.DiagnosticSeverity.Warning => OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity.Warning,
			Camp.Compiler.DiagnosticSeverity.Info => OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity.Information,
			_ => OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity.Error
			},
			Code = diagnostic.Code is null ? null : new DiagnosticCode(diagnostic.Code),
			Source = "camp",
			Message = diagnostic.Message
		};
#pragma warning restore CS8625
	}

	public static DocumentSymbol ToLspDocumentSymbol(CampDocumentSymbol symbol)
	{
		return new DocumentSymbol
		{
			Name = symbol.Name,
			Kind = ToLspSymbolKind(symbol.Kind),
			Detail = symbol.Detail,
			Range = ToLspRange(symbol.Range),
			SelectionRange = ToLspRange(symbol.SelectionRange),
			Children = new Container<DocumentSymbol>(symbol.Children.Select(ToLspDocumentSymbol))
		};
	}

	public static WorkspaceSymbol ToLspWorkspaceSymbol(CampWorkspaceSymbol symbol)
	{
		return new WorkspaceSymbol
		{
			Name = symbol.Name,
			Kind = ToLspSymbolKind(symbol.Kind),
			Location = new Location
			{
				Uri = DocumentUri.FromFileSystemPath(symbol.Location.Path),
				Range = ToLspRange(symbol.Location.Range)
			},
			ContainerName = symbol.ContainerName
		};
	}

	static OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind ToLspSymbolKind(CampSymbolKind kind)
	{
		return kind switch
		{
			CampSymbolKind.Type => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Class,
			CampSymbolKind.Function => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Function,
			CampSymbolKind.Method => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Method,
			CampSymbolKind.Field => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Field,
			CampSymbolKind.Variable => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Variable,
			CampSymbolKind.Parameter => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Variable,
			CampSymbolKind.EnumValue => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.EnumMember,
			CampSymbolKind.Alias => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Interface,
			_ => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Object
		};
	}

	public static class Protocol
	{
		public static readonly TextDocumentSelector DocumentSelector = new(new TextDocumentFilter
		{
			Language = "camp",
			Scheme = "file",
			Pattern = "**/*.camp"
		});
	}
}
