using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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

using CampLspTrace trace = CampLspTrace.Create();
trace.Write("server.start", ("tracePath", trace.Path), ("processId", Environment.ProcessId));
CampLspWorkspace workspace = new(trace);
LanguageServer server = await LanguageServer.From(options => options
	.WithInput(Console.OpenStandardInput())
	.WithOutput(Console.OpenStandardOutput())
	.AddHandler(new CampTextDocumentSyncHandler(workspace))
	.AddHandler(new CampCompletionHandler(workspace))
	.AddHandler(new CampHoverHandler(workspace))
	.AddHandler(new CampSignatureHelpHandler(workspace))
	.AddHandler(new CampDefinitionHandler(workspace))
	.AddHandler(new CampReferencesHandler(workspace))
	.AddHandler(new CampDocumentSymbolHandler(workspace))
	.AddHandler(new CampWorkspaceSymbolHandler(workspace))
	.AddHandler(new CampCodeLensHandler(workspace))
	.OnStarted((languageServer, _) =>
	{
		workspace.SetLanguageServer(languageServer);
		return Task.CompletedTask;
	}));
await server.WaitForExit;
trace.Write("server.stop");

sealed class CampTextDocumentSyncHandler(CampLspWorkspace workspace) : TextDocumentSyncHandlerBase
{
	public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
	{
		return new TextDocumentAttributes(uri, "camp");
	}

	public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
	{
		workspace.Open(request.TextDocument.Uri, request.TextDocument.Text, request.TextDocument.Version);
		return Unit.Task;
	}

	public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
	{
		string text = request.ContentChanges.LastOrDefault()?.Text ?? "";
		workspace.Change(request.TextDocument.Uri, text, request.TextDocument.Version);
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

sealed class CampCompletionHandler(CampLspWorkspace workspace) : CompletionHandlerBase<CampCompletionIdentity>
{
	protected override Task<CompletionList<CampCompletionIdentity>> HandleParams(CompletionParams request, CancellationToken cancellationToken)
	{
		IReadOnlyList<CampCompletionItem> completions = workspace.GetCompletions(request.TextDocument.Uri, CampLsp.ToCampPosition(request.Position), request.Context?.TriggerCharacter);
		return Task.FromResult(new CompletionList<CampCompletionIdentity>(isIncomplete: false, completions.Select(CampLsp.ToLspCompletionItem)));
	}

	protected override Task<CompletionItem<CampCompletionIdentity>> HandleResolve(CompletionItem<CampCompletionIdentity> request, CancellationToken cancellationToken)
	{
		return Task.FromResult(request);
	}

	protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
	{
		return new CompletionRegistrationOptions
		{
			DocumentSelector = CampLsp.Protocol.DocumentSelector,
			TriggerCharacters = new Container<string>(".", " ")
		};
	}
}

public sealed class CampCompletionIdentity : IHandlerIdentity
{
	public string __identity { get; init; } = "camp";
}

public sealed record CampTestCommandArgument(
	string Project,
	string Cwd,
	string Filter,
	string Sourcefile,
	int Sourceline);

public sealed record CampTestCodeLens(
	CampTextRange Range,
	string Title,
	string Command,
	CampTestCommandArgument Argument);

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

sealed class CampSignatureHelpHandler(CampLspWorkspace workspace) : SignatureHelpHandlerBase
{
	public override Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
	{
		CampSignatureHelp? signatureHelp = workspace.GetSignatureHelp(request.TextDocument.Uri, CampLsp.ToCampPosition(request.Position));
		return Task.FromResult(signatureHelp is null ? null : CampLsp.ToLspSignatureHelp(signatureHelp));
	}

	protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
	{
		return new SignatureHelpRegistrationOptions
		{
			DocumentSelector = CampLsp.Protocol.DocumentSelector,
			TriggerCharacters = new Container<string>("(", ",")
		};
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
sealed class CampReferencesHandler(CampLspWorkspace workspace) : ReferencesHandlerBase
{
	public override Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken)
	{
		IReadOnlyList<CampReference> references = workspace.GetReferences(
			request.TextDocument.Uri,
			CampLsp.ToCampPosition(request.Position),
			request.Context.IncludeDeclaration);
		return Task.FromResult<LocationContainer?>(new LocationContainer(references.Select(CampLsp.ToLspLocation)));
	}

	protected override ReferenceRegistrationOptions CreateRegistrationOptions(ReferenceCapability capability, ClientCapabilities clientCapabilities)
	{
		return new ReferenceRegistrationOptions { DocumentSelector = CampLsp.Protocol.DocumentSelector };
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

#pragma warning disable CS8609
sealed class CampCodeLensHandler(CampLspWorkspace workspace) : CodeLensHandlerBase
{
	public override Task<CodeLensContainer> Handle(CodeLensParams request, CancellationToken cancellationToken)
	{
		IReadOnlyList<CampTestCodeLens> lenses = workspace.GetTestCodeLenses(request.TextDocument.Uri);
		return Task.FromResult(new CodeLensContainer(lenses.Select(ToCodeLens)));
	}

	public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
	{
		return Task.FromResult(request);
	}

	protected override CodeLensRegistrationOptions CreateRegistrationOptions(CodeLensCapability capability, ClientCapabilities clientCapabilities)
	{
		return new CodeLensRegistrationOptions { DocumentSelector = CampLsp.Protocol.DocumentSelector };
	}

	static CodeLens ToCodeLens(CampTestCodeLens lens)
	{
		return new CodeLens
		{
			Range = CampLsp.ToLspRange(lens.Range),
			Command = new Command
			{
				Title = lens.Title,
				Name = lens.Command
			}.WithArguments(lens.Argument)
		};
	}
}
#pragma warning restore CS8609

public sealed class CampLspWorkspace
{
	const int DiagnosticDebounceMilliseconds = 500;
	const int QueryWarmWaitMilliseconds = 75;
	readonly Dictionary<string, OpenDocument> openDocuments = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, CampAnalysisSnapshot> diagnosticSnapshots = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, CampAnalysisSnapshot> querySnapshots = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, CachedQueryService> queryServiceCache = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, QueryServiceBuild> queryServiceBuilds = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, int?> latestRequestedDiagnosticVersions = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, int?> latestCompletedDiagnosticVersions = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, int?> inFlightDiagnosticVersions = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, CancellationTokenSource> pendingDiagnostics = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, CachedProjectRequest> projectRequestCache = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, string> lastPublishedDiagnosticKeys = new(StringComparer.OrdinalIgnoreCase);
	readonly SemaphoreSlim analysisGate = new(1, 1);
	readonly object gate = new();
	readonly CampLspTrace trace;
	ILanguageServer? languageServer;

	public CampLspWorkspace(CampLspTrace trace)
	{
		this.trace = trace;
	}

	public void SetLanguageServer(ILanguageServer server)
	{
		languageServer = server;
		trace.Write("server.ready");
		List<DocumentUri> uris;
		lock (gate)
			uris = openDocuments.Values.Select(static document => document.Uri).ToList();
		foreach (DocumentUri uri in uris)
			PublishDiagnostics(uri);
	}

	public void Open(DocumentUri uri, string text, int? version)
	{
		string path = uri.GetFileSystemPath();
		trace.Write("document.open", ("file", path), ("version", version), ("length", text.Length));
		lock (gate)
			openDocuments[path] = new OpenDocument(uri, path, text, version);
		Reanalyze(uri);
	}

	public void Change(DocumentUri uri, string text, int? version)
	{
		string path = uri.GetFileSystemPath();
		trace.Write("document.change", ("file", path), ("version", version), ("length", text.Length));
		lock (gate)
			openDocuments[path] = new OpenDocument(uri, path, text, version);
		ScheduleDiagnostics(uri, path, version);
	}

	public void Close(DocumentUri uri)
	{
		string path = uri.GetFileSystemPath();
		trace.Write("document.close", ("file", path));
		CancelPendingDiagnostics(path);
		lock (gate)
		{
			openDocuments.Remove(path);
			diagnosticSnapshots.Remove(path);
			querySnapshots.Remove(path);
			queryServiceCache.Remove(path);
			queryServiceBuilds.Remove(path);
			latestRequestedDiagnosticVersions.Remove(path);
			latestCompletedDiagnosticVersions.Remove(path);
			inFlightDiagnosticVersions.Remove(path);
			lastPublishedDiagnosticKeys.Remove(path);
		}
		PublishDiagnostics(uri, [], force: true);
	}

	public void Reanalyze(DocumentUri uri)
	{
		string path = uri.GetFileSystemPath();
		long start = Stopwatch.GetTimestamp();
		CancelPendingDiagnostics(path);
		OpenDocument? document;
		lock (gate)
		{
			openDocuments.TryGetValue(path, out document);
			latestRequestedDiagnosticVersions[path] = document?.Version;
			inFlightDiagnosticVersions[path] = document?.Version;
		}
		if (document is null)
		{
			trace.Write("analysis.skip", ("file", path), ("reason", "document-missing"));
			return;
		}
		CampAnalysisSnapshot? snapshot = AnalyzeSingleFlight(document, CancellationToken.None);
		if (snapshot is null)
		{
			trace.Write("analysis.skip", ("file", path), ("version", document.Version), ("reason", "canceled"));
			return;
		}
		bool warmQueryService;
		lock (gate)
		{
			diagnosticSnapshots[path] = snapshot;
			latestCompletedDiagnosticVersions[path] = document.Version;
			inFlightDiagnosticVersions.Remove(path);
			warmQueryService = snapshot.Success;
			if (snapshot.Success)
				querySnapshots[path] = snapshot;
		}
		if (warmQueryService)
			WarmQueryService(path, snapshot);
		trace.Write("analysis.complete",
			("file", path),
			("version", document.Version),
			("success", snapshot.Success),
			("diagnosticCount", snapshot.Diagnostics.Count),
			("durationMs", ElapsedMilliseconds(start)));
		PublishDiagnostics(uri, snapshot.Diagnostics.Where(diagnostic => string.IsNullOrWhiteSpace(diagnostic.Path) || string.Equals(diagnostic.Path, path, StringComparison.OrdinalIgnoreCase)));
	}

	void ScheduleDiagnostics(DocumentUri uri, string path, int? version)
	{
		CancellationTokenSource source = new();
		lock (gate)
		{
			if (pendingDiagnostics.Remove(path, out CancellationTokenSource? previous))
			{
				previous.Cancel();
				trace.Write("diagnostics.debounce.cancel", ("file", path));
			}
			pendingDiagnostics[path] = source;
			latestRequestedDiagnosticVersions[path] = version;
		}
		trace.Write("diagnostics.debounce.schedule", ("file", path), ("version", version), ("delayMs", DiagnosticDebounceMilliseconds));

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(DiagnosticDebounceMilliseconds, source.Token).ConfigureAwait(false);
				if (!source.Token.IsCancellationRequested)
				{
					trace.Write("diagnostics.debounce.fire", ("file", path), ("version", version));
					await ReanalyzeIfCurrentAsync(uri, path, version, source).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
			}
			finally
			{
				lock (gate)
				{
					if (pendingDiagnostics.TryGetValue(path, out CancellationTokenSource? current) && ReferenceEquals(current, source))
						pendingDiagnostics.Remove(path);
				}
				source.Dispose();
			}
		});
	}

	async Task ReanalyzeIfCurrentAsync(DocumentUri uri, string path, int? version, CancellationTokenSource source)
	{
		long start = Stopwatch.GetTimestamp();
		OpenDocument? document;
		bool alreadyCompleted;
		lock (gate)
		{
			if (!pendingDiagnostics.TryGetValue(path, out CancellationTokenSource? current) || !ReferenceEquals(current, source))
			{
				trace.Write("analysis.skip", ("file", path), ("version", version), ("reason", "debounce-superseded"));
				return;
			}
			if (!openDocuments.TryGetValue(path, out document))
			{
				trace.Write("analysis.skip", ("file", path), ("version", version), ("reason", "document-missing"));
				return;
			}
			if (version is not null && document.Version != version)
			{
				trace.Write("analysis.skip", ("file", path), ("version", version), ("currentVersion", document.Version), ("reason", "version-stale"));
				return;
			}
			alreadyCompleted = version is not null
				&& latestCompletedDiagnosticVersions.TryGetValue(path, out int? completedVersion)
				&& completedVersion == version
				&& diagnosticSnapshots.ContainsKey(path);
			if (alreadyCompleted)
			{
				trace.Write("analysis.skip", ("file", path), ("version", version), ("reason", "version-completed"));
				return;
			}
			inFlightDiagnosticVersions[path] = version;
		}

		CampAnalysisSnapshot? snapshot = await AnalyzeSingleFlightAsync(document, source.Token).ConfigureAwait(false);
		if (snapshot is null)
		{
			lock (gate)
				if (inFlightDiagnosticVersions.TryGetValue(path, out int? inFlightVersion) && inFlightVersion == version)
					inFlightDiagnosticVersions.Remove(path);
			trace.Write("analysis.skip", ("file", path), ("version", version), ("reason", "canceled"));
			return;
		}
		bool warmQueryService;
		lock (gate)
		{
			if (!openDocuments.TryGetValue(path, out OpenDocument? currentDocument) || version is not null && currentDocument.Version != version)
			{
				if (inFlightDiagnosticVersions.TryGetValue(path, out int? inFlightVersion) && inFlightVersion == version)
					inFlightDiagnosticVersions.Remove(path);
				trace.Write("analysis.skip", ("file", path), ("version", version), ("reason", "completed-stale"));
				return;
			}
			diagnosticSnapshots[path] = snapshot;
			latestCompletedDiagnosticVersions[path] = document.Version;
			inFlightDiagnosticVersions.Remove(path);
			warmQueryService = snapshot.Success;
			if (snapshot.Success)
				querySnapshots[path] = snapshot;
		}
		if (warmQueryService)
			WarmQueryService(path, snapshot);
		trace.Write("analysis.complete",
			("file", path),
			("version", document.Version),
			("success", snapshot.Success),
			("diagnosticCount", snapshot.Diagnostics.Count),
			("durationMs", ElapsedMilliseconds(start)));
		PublishDiagnostics(uri, snapshot.Diagnostics.Where(diagnostic => string.IsNullOrWhiteSpace(diagnostic.Path) || string.Equals(diagnostic.Path, path, StringComparison.OrdinalIgnoreCase)));
	}

	void CancelPendingDiagnostics(string path)
	{
		lock (gate)
		{
			if (!pendingDiagnostics.Remove(path, out CancellationTokenSource? source))
				return;
			source.Cancel();
		}
		trace.Write("diagnostics.debounce.cancel", ("file", path));
	}

	public CampHover? GetHover(DocumentUri uri, CampTextPosition position)
	{
		long start = Stopwatch.GetTimestamp();
		if (!TryGetQuerySnapshot(uri, out string path, out _, out CampAnalysisSnapshot? snapshot))
		{
			trace.Write("query.hover", ("file", path), ("snapshot", "missing"), ("durationMs", ElapsedMilliseconds(start)));
			return null;
		}
		if (!TryGetQueryService(path, snapshot!, allowStale: true, out CachedQueryService? service, out string cacheState))
		{
			trace.Write("query.hover", ("file", path), ("snapshot", cacheState), ("hasResult", false), ("durationMs", ElapsedMilliseconds(start)));
			return null;
		}
		CampHover? result = service!.Service.GetHover(path, position);
		trace.Write("query.hover", ("file", path), ("snapshot", cacheState), ("hasResult", result is not null), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	public CampSymbolLocation? GetDefinition(DocumentUri uri, CampTextPosition position)
	{
		long start = Stopwatch.GetTimestamp();
		if (!TryGetQuerySnapshot(uri, out string path, out _, out CampAnalysisSnapshot? snapshot))
		{
			trace.Write("query.definition", ("file", path), ("snapshot", "missing"), ("durationMs", ElapsedMilliseconds(start)));
			return null;
		}
		if (!TryGetQueryService(path, snapshot!, allowStale: false, out CachedQueryService? service, out string cacheState))
		{
			trace.Write("query.definition", ("file", path), ("snapshot", cacheState), ("hasResult", false), ("durationMs", ElapsedMilliseconds(start)));
			return null;
		}
		CampSymbolLocation? result = service!.Service.GetDefinition(path, position);
		trace.Write("query.definition", ("file", path), ("snapshot", cacheState), ("hasResult", result is not null), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	public IReadOnlyList<CampReference> GetReferences(DocumentUri uri, CampTextPosition position, bool includeDeclaration)
	{
		long start = Stopwatch.GetTimestamp();
		if (!TryGetQuerySnapshot(uri, out string path, out _, out CampAnalysisSnapshot? snapshot))
		{
			trace.Write("query.references", ("file", path), ("snapshot", "missing"), ("durationMs", ElapsedMilliseconds(start)));
			return [];
		}
		if (!TryGetQueryService(path, snapshot!, allowStale: false, out CachedQueryService? service, out string cacheState))
		{
			trace.Write("query.references", ("file", path), ("snapshot", cacheState), ("resultCount", 0), ("durationMs", ElapsedMilliseconds(start)));
			return [];
		}
		IReadOnlyList<CampReference> result = service!.Service.GetReferences(path, position, includeDeclaration);
		trace.Write("query.references", ("file", path), ("snapshot", cacheState), ("resultCount", result.Count), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	public CampSignatureHelp? GetSignatureHelp(DocumentUri uri, CampTextPosition position)
	{
		long start = Stopwatch.GetTimestamp();
		if (!TryGetQuerySnapshot(uri, out string path, out OpenDocument? document, out CampAnalysisSnapshot? snapshot))
		{
			trace.Write("query.signatureHelp", ("file", path), ("snapshot", "missing"), ("durationMs", ElapsedMilliseconds(start)));
			return null;
		}
		if (!TryGetQueryService(path, snapshot!, allowStale: true, out CachedQueryService? service, out string cacheState))
		{
			trace.Write("query.signatureHelp", ("file", path), ("snapshot", cacheState), ("hasResult", false), ("durationMs", ElapsedMilliseconds(start)));
			return null;
		}
		CampSignatureHelp? result = service!.Service.GetSignatureHelp(path, position, document?.Text);
		trace.Write("query.signatureHelp", ("file", path), ("snapshot", cacheState), ("hasResult", result is not null), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	public IReadOnlyList<CampCompletionItem> GetCompletions(DocumentUri uri, CampTextPosition position, string? triggerCharacter = null)
	{
		long start = Stopwatch.GetTimestamp();
		if (!TryGetQuerySnapshot(uri, out string path, out OpenDocument? document, out CampAnalysisSnapshot? snapshot))
		{
			IReadOnlyList<CampCompletionItem> fallback = document is null ? [] : GetFallbackCompletions(document.Text, position);
			trace.Write("query.completion", ("file", path), ("snapshot", "fallback"), ("resultCount", fallback.Count), ("durationMs", ElapsedMilliseconds(start)));
			return fallback;
		}
		if (!TryGetQueryService(path, snapshot!, allowStale: true, out CachedQueryService? service, out string cacheState))
		{
			IReadOnlyList<CampCompletionItem> fallback = document is null ? [] : GetFallbackCompletions(document.Text, position);
			trace.Write("query.completion", ("file", path), ("snapshot", cacheState), ("resultCount", fallback.Count), ("durationMs", ElapsedMilliseconds(start)));
			return fallback;
		}
		IReadOnlyList<CampCompletionItem> result = service!.Service.GetCompletions(path, position, document?.Text, requireFinallyForWhitespaceTrigger: triggerCharacter == " ");
		trace.Write("query.completion", ("file", path), ("snapshot", cacheState), ("resultCount", result.Count), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	public IReadOnlyList<CampDocumentSymbol> GetDocumentSymbols(DocumentUri uri)
	{
		long start = Stopwatch.GetTimestamp();
		if (!TryGetQuerySnapshot(uri, out string path, out _, out CampAnalysisSnapshot? snapshot))
		{
			trace.Write("query.documentSymbols", ("file", path), ("snapshot", "missing"), ("durationMs", ElapsedMilliseconds(start)));
			return [];
		}
		if (!TryGetQueryService(path, snapshot!, allowStale: true, out CachedQueryService? service, out string cacheState))
		{
			trace.Write("query.documentSymbols", ("file", path), ("snapshot", cacheState), ("resultCount", 0), ("durationMs", ElapsedMilliseconds(start)));
			return [];
		}
		IReadOnlyList<CampDocumentSymbol> result = service!.GetDocumentSymbols(path);
		trace.Write("query.documentSymbols", ("file", path), ("snapshot", cacheState), ("resultCount", result.Count), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	public IReadOnlyList<CampWorkspaceSymbol> GetWorkspaceSymbols(string query)
	{
		long start = Stopwatch.GetTimestamp();
		List<(string Path, CampAnalysisSnapshot Snapshot)> snapshotList;
		lock (gate)
			snapshotList = querySnapshots.Select(static pair => (pair.Key, pair.Value)).ToList();
		IReadOnlyList<CampWorkspaceSymbol> result = snapshotList
			.SelectMany(pair => TryGetQueryService(pair.Path, pair.Snapshot, allowStale: true, out CachedQueryService? service, out _) ? service!.Service.GetWorkspaceSymbols(query) : Enumerable.Empty<CampWorkspaceSymbol>())
			.DistinctBy(static symbol => (symbol.Name, symbol.Kind, symbol.Location.Path, symbol.Location.Range.Start.Line, symbol.Location.Range.Start.Character))
			.OrderBy(static symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(static symbol => symbol.Location.Path, StringComparer.OrdinalIgnoreCase)
			.ThenBy(static symbol => symbol.Location.Range.Start.Line)
			.ThenBy(static symbol => symbol.Location.Range.Start.Character)
			.ToList();
		trace.Write("query.workspaceSymbols", ("query", query), ("snapshotCount", snapshotList.Count), ("resultCount", result.Count), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	public IReadOnlyList<CampTestCodeLens> GetTestCodeLenses(DocumentUri uri)
	{
		long start = Stopwatch.GetTimestamp();
		string path = uri.GetFileSystemPath();
		OpenDocument? document;
		lock (gate)
			openDocuments.TryGetValue(path, out document);
		if (document is null)
		{
			trace.Write("query.testCodeLens", ("file", path), ("snapshot", "document-missing"), ("resultCount", 0), ("durationMs", ElapsedMilliseconds(start)));
			return [];
		}

		CompilerRequest request = CreateRequest(path);
		CampTestDiscoverySnapshot snapshot = CampLanguageService.DiscoverTests(request, [new CampSourceOverlay(document.Path, document.Text, document.Version ?? 0)]);
		string project = GetProjectTargetPath(path);
		string cwd = Path.GetDirectoryName(project) ?? Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
		List<CampTestCodeLens> result = [];
		foreach (CampDiscoveredTest test in snapshot.Tests)
		{
			if (!string.Equals(Path.GetFullPath(test.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
				continue;
			CampTestCommandArgument argument = new(project, cwd, test.Id, test.Sourcefile, test.Sourceline);
			result.Add(new CampTestCodeLens(test.Range, "Run Current Test", "camp.test.run", argument));
			result.Add(new CampTestCodeLens(test.Range, "Debug Current Test", "camp.test.debug", argument));
			result.Add(new CampTestCodeLens(test.Range, "Cover Current Test", "camp.test.cover", argument));
		}
		trace.Write("query.testCodeLens", ("file", path), ("resultCount", result.Count), ("durationMs", ElapsedMilliseconds(start)));
		return result;
	}

	void WarmQueryService(string path, CampAnalysisSnapshot snapshot)
	{
		lock (gate)
			StartQueryServiceBuildNoLock(path, snapshot);
	}

	bool TryGetQueryService(string path, CampAnalysisSnapshot snapshot, bool allowStale, out CachedQueryService? service, out string state)
	{
		lock (gate)
		{
			if (queryServiceCache.TryGetValue(path, out CachedQueryService? cached) && ReferenceEquals(cached.Snapshot, snapshot))
			{
				service = cached;
				state = "query";
				return true;
			}
			if (allowStale && queryServiceCache.TryGetValue(path, out cached))
			{
				service = cached;
				state = "stale-query";
				StartQueryServiceBuildNoLock(path, snapshot);
				return true;
			}
			StartQueryServiceBuildNoLock(path, snapshot);
		}
		if (TryWaitForQueryService(path, snapshot, allowStale, out service, out state))
			return true;
		service = BuildQueryServiceSynchronously(path, snapshot);
		state = "query-sync";
		return true;
	}

	CachedQueryService BuildQueryServiceSynchronously(string path, CampAnalysisSnapshot snapshot)
	{
		long start = Stopwatch.GetTimestamp();
		CachedQueryService created = new(snapshot);
		lock (gate)
		{
			if (queryServiceCache.TryGetValue(path, out CachedQueryService? cached) && ReferenceEquals(cached.Snapshot, snapshot))
				return cached;
			queryServiceCache[path] = created;
			if (queryServiceBuilds.TryGetValue(path, out QueryServiceBuild? build) && ReferenceEquals(build.Snapshot, snapshot))
				queryServiceBuilds.Remove(path);
		}
		trace.Write("queryService.build", ("file", path), ("mode", "sync"), ("accepted", true), ("durationMs", ElapsedMilliseconds(start)));
		return created;
	}

	bool TryWaitForQueryService(string path, CampAnalysisSnapshot snapshot, bool allowStale, out CachedQueryService? service, out string state)
	{
		long start = Stopwatch.GetTimestamp();
		while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < QueryWarmWaitMilliseconds)
		{
			Thread.Sleep(5);
			lock (gate)
			{
				if (queryServiceCache.TryGetValue(path, out CachedQueryService? cached) && ReferenceEquals(cached.Snapshot, snapshot))
				{
					service = cached;
					state = "query";
					return true;
				}
				if (allowStale && queryServiceCache.TryGetValue(path, out cached))
				{
					service = cached;
					state = "stale-query";
					return true;
				}
			}
		}
		service = null;
		state = "warming";
		return false;
	}

	void StartQueryServiceBuildNoLock(string path, CampAnalysisSnapshot snapshot)
	{
		if (queryServiceCache.TryGetValue(path, out CachedQueryService? cached) && ReferenceEquals(cached.Snapshot, snapshot))
			return;
		if (queryServiceBuilds.TryGetValue(path, out QueryServiceBuild? existing) && ReferenceEquals(existing.Snapshot, snapshot))
			return;
		QueryServiceBuild build = new(snapshot);
		queryServiceBuilds[path] = build;
		_ = Task.Run(() => BuildQueryServiceInBackground(path, snapshot, build));
	}

	void BuildQueryServiceInBackground(string path, CampAnalysisSnapshot snapshot, QueryServiceBuild build)
	{
		long start = Stopwatch.GetTimestamp();
		try
		{
			CachedQueryService created = new(snapshot);
			bool accepted = false;
			lock (gate)
			{
				if (queryServiceBuilds.TryGetValue(path, out QueryServiceBuild? currentBuild) && ReferenceEquals(currentBuild, build))
				{
					queryServiceBuilds.Remove(path);
					if (querySnapshots.TryGetValue(path, out CampAnalysisSnapshot? currentSnapshot) && ReferenceEquals(currentSnapshot, snapshot))
					{
						queryServiceCache[path] = created;
						accepted = true;
					}
				}
			}
			trace.Write(accepted ? "queryService.build" : "queryService.build.discarded", ("file", path), ("mode", "async"), ("accepted", accepted), ("durationMs", ElapsedMilliseconds(start)));
		}
		catch (Exception ex)
		{
			lock (gate)
				if (queryServiceBuilds.TryGetValue(path, out QueryServiceBuild? currentBuild) && ReferenceEquals(currentBuild, build))
					queryServiceBuilds.Remove(path);
			trace.Write("queryService.build.error", ("file", path), ("message", ex.Message), ("durationMs", ElapsedMilliseconds(start)));
		}
	}

	bool TryGetQuerySnapshot(DocumentUri uri, out string path, out OpenDocument? document, out CampAnalysisSnapshot? snapshot)
	{
		path = uri.GetFileSystemPath();
		lock (gate)
		{
			openDocuments.TryGetValue(path, out document);
			querySnapshots.TryGetValue(path, out snapshot);
		}
		return snapshot is not null;
	}

	static IReadOnlyList<CampCompletionItem> GetFallbackCompletions(string text, CampTextPosition position)
	{
		CompletionTextContext context = GetCompletionTextContext(text, position);
		if (context.IsMember)
			return [];

		HashSet<string> labels = new(StringComparer.OrdinalIgnoreCase);
		List<CampCompletionItem> completions = [];
		foreach (string identifier in ScanIdentifiers(text))
		{
			if (IsCompletionKeyword(identifier) || !MatchesCompletionPrefix(identifier, context.Prefix) || !labels.Add(identifier))
				continue;
			completions.Add(new CampCompletionItem(identifier, CampSymbolKind.Variable, "Text match", null));
		}
		foreach (string keyword in CompletionKeywords())
		{
			if (MatchesCompletionPrefix(keyword, context.Prefix) && labels.Add(keyword))
				completions.Add(new CampCompletionItem(keyword, CampSymbolKind.Keyword, null, null));
		}
		return completions
			.OrderBy(static item => item.Kind == CampSymbolKind.Keyword ? 1 : 0)
			.ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	static CompletionTextContext GetCompletionTextContext(string text, CampTextPosition position)
	{
		string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
		if (position.Line < 0 || position.Line >= lines.Length)
			return new CompletionTextContext("", false);
		string line = lines[position.Line];
		int cursor = Math.Clamp(position.Character, 0, line.Length);
		int prefixStart = cursor;
		while (prefixStart > 0 && IsIdentifierPart(line[prefixStart - 1]))
			prefixStart--;
		string prefix = line[prefixStart..cursor];
		int dot = prefixStart - 1;
		while (dot >= 0 && char.IsWhiteSpace(line[dot]))
			dot--;
		return new CompletionTextContext(prefix, dot >= 0 && line[dot] == '.');
	}

	static IEnumerable<string> ScanIdentifiers(string text)
	{
		for (int i = 0; i < text.Length;)
		{
			if (!IsIdentifierStart(text[i]))
			{
				i++;
				continue;
			}
			int start = i++;
			while (i < text.Length && IsIdentifierPart(text[i]))
				i++;
			yield return text[start..i];
		}
	}

	static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

	static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

	static bool MatchesCompletionPrefix(string value, string prefix)
	{
		return prefix.Length == 0 || value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
	}

	static bool IsCompletionKeyword(string value)
	{
		return CompletionKeywords().Contains(value, StringComparer.Ordinal);
	}

	static IEnumerable<string> CompletionKeywords()
	{
		return
		[
			"if", "else", "while", "for", "foreach", "return", "try", "catch", "finally",
			"new", "init", "default", "true", "false", "null", "using", "export",
			"class", "struct", "interface", "enum", "newtype", "delegate", "fn"
		];
	}

	CampAnalysisSnapshot Analyze(OpenDocument document)
	{
		long start = Stopwatch.GetTimestamp();
		CompilerRequest request = CreateRequest(document.Path);
		CampSourceOverlay[] overlays = [new CampSourceOverlay(document.Path, document.Text, document.Version ?? 0)];
		CampAnalysisSnapshot snapshot = CampLanguageService.Analyze(request, overlays);
		if (ContainsTestAttributes(document.Text))
		{
			CampTestDiscoverySnapshot testSnapshot = CampLanguageService.DiscoverTests(request, overlays);
			snapshot = new CampAnalysisSnapshot
			{
				Compilation = snapshot.Compilation,
				Diagnostics = MergeDiagnostics(snapshot.Diagnostics, testSnapshot.Diagnostics)
			};
		}
		trace.Write("analysis.pipeline",
			("file", document.Path),
			("version", document.Version),
			("fileCount", snapshot.Compilation.Files.Count),
			("diagnosticCount", snapshot.Diagnostics.Count),
			("success", snapshot.Success),
			("durationMs", ElapsedMilliseconds(start)));
		return snapshot;
	}

	static bool ContainsTestAttributes(string text)
	{
		return text.Contains("@test", StringComparison.Ordinal)
			|| text.Contains("@testonly", StringComparison.Ordinal)
			|| text.Contains("@skip", StringComparison.Ordinal);
	}

	static IReadOnlyList<CampSourceDiagnostic> MergeDiagnostics(IReadOnlyList<CampSourceDiagnostic> left, IReadOnlyList<CampSourceDiagnostic> right)
	{
		return left.Concat(right)
			.GroupBy(static diagnostic => (
				diagnostic.Path,
				StartLine: diagnostic.Range?.Start.Line ?? -1,
				StartCharacter: diagnostic.Range?.Start.Character ?? -1,
				EndLine: diagnostic.Range?.End.Line ?? -1,
				EndCharacter: diagnostic.Range?.End.Character ?? -1,
				diagnostic.Message,
				diagnostic.Code,
				diagnostic.Severity))
			.Select(static group => group.First())
			.ToList();
	}

	CampAnalysisSnapshot? AnalyzeSingleFlight(OpenDocument document, CancellationToken cancellationToken)
	{
		long waitStart = Stopwatch.GetTimestamp();
		try
		{
			analysisGate.Wait(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return null;
		}

		try
		{
			trace.Write("analysis.gate.acquire", ("file", document.Path), ("version", document.Version), ("waitMs", ElapsedMilliseconds(waitStart)));
			return cancellationToken.IsCancellationRequested ? null : Analyze(document);
		}
		finally
		{
			analysisGate.Release();
		}
	}

	async Task<CampAnalysisSnapshot?> AnalyzeSingleFlightAsync(OpenDocument document, CancellationToken cancellationToken)
	{
		long waitStart = Stopwatch.GetTimestamp();
		try
		{
			await analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return null;
		}

		try
		{
			trace.Write("analysis.gate.acquire", ("file", document.Path), ("version", document.Version), ("waitMs", ElapsedMilliseconds(waitStart)));
			return cancellationToken.IsCancellationRequested ? null : Analyze(document);
		}
		finally
		{
			analysisGate.Release();
		}
	}

	CompilerRequest CreateRequest(string documentPath)
	{
		long start = Stopwatch.GetTimestamp();
		string? buildFile = CampProjectLoader.FindNearestBuildFile(documentPath);
		if (buildFile is not null)
		{
			string canonicalBuildFile = Path.GetFullPath(buildFile);
			lock (gate)
				if (projectRequestCache.TryGetValue(canonicalBuildFile, out CachedProjectRequest? cached) && cached.IsCurrent())
				{
					trace.Write("project.load", ("file", documentPath), ("buildFile", canonicalBuildFile), ("cache", "hit"), ("durationMs", ElapsedMilliseconds(start)));
					return CloneRequest(cached.Request);
				}

			string root = Path.GetDirectoryName(buildFile) ?? Directory.GetCurrentDirectory();
			CampProjectLoadResult result = CampProjectLoader.LoadBuildFile(buildFile, CampProjectEnvironment.Create(root), CampProjectCommandKind.LanguageService);
			if (result.Success)
			{
				lock (gate)
					projectRequestCache[canonicalBuildFile] = CachedProjectRequest.Create(canonicalBuildFile, result.Request);
				trace.Write("project.load",
					("file", documentPath),
					("buildFile", canonicalBuildFile),
					("cache", "miss"),
					("sourceCount", result.Request.Files.Count + result.Request.AnalysisSourceFiles.Count + result.Request.IncludeFiles.Count),
					("durationMs", ElapsedMilliseconds(start)));
				return result.Request;
			}
			trace.Write("project.load", ("file", documentPath), ("buildFile", canonicalBuildFile), ("cache", "miss"), ("success", false), ("durationMs", ElapsedMilliseconds(start)));
		}

		string workingDirectory = Path.GetDirectoryName(documentPath) ?? Directory.GetCurrentDirectory();
		CompilerRequest request = new()
		{
			RuntimeRoot = AppContext.BaseDirectory,
			WorkingDirectory = workingDirectory
		};
		request.Files.Add(Path.GetFileName(documentPath));
		trace.Write("project.load", ("file", documentPath), ("cache", "loose-file"), ("durationMs", ElapsedMilliseconds(start)));
		return request;
	}

	static string GetProjectTargetPath(string documentPath)
	{
		string? buildFile = CampProjectLoader.FindNearestBuildFile(documentPath);
		return Path.GetFullPath(buildFile ?? documentPath);
	}

	static CompilerRequest CloneRequest(CompilerRequest source)
	{
		CompilerRequest clone = new()
		{
			RuntimeRoot = source.RuntimeRoot,
			WorkingDirectory = source.WorkingDirectory,
			TargetName = source.TargetName,
			ProfileName = source.ProfileName,
			EmitKind = source.EmitKind,
			Inspect = source.Inspect,
			Xml = source.Xml,
			InspectApi = source.InspectApi,
			BuildKind = source.BuildKind,
			InferBuildKind = source.InferBuildKind,
			CommandMode = source.CommandMode,
			DeclarationParticipationMode = source.DeclarationParticipationMode,
			CoverageInstrumentationMode = source.CoverageInstrumentationMode,
			EmitDebugInfo = source.EmitDebugInfo,
			EmitMetadata = source.EmitMetadata,
			OutDir = source.OutDir,
			ProjectName = source.ProjectName,
			SubsystemName = source.SubsystemName,
			NoStdLib = source.NoStdLib,
			WithinAllocationPolicy = source.WithinAllocationPolicy,
			SourcefilePathMode = source.SourcefilePathMode,
			SourcefileDefaultRoot = source.SourcefileDefaultRoot,
			ListTests = source.ListTests,
			TestOutputDir = source.TestOutputDir,
			TestResultFormat = source.TestResultFormat,
			CoverageOutputDir = source.CoverageOutputDir,
			CoverageFormat = source.CoverageFormat,
			TargetRoot = source.TargetRoot,
			PackageSourceRoot = source.PackageSourceRoot,
			PackageArtifactRoot = source.PackageArtifactRoot
		};
		clone.Files.AddRange(source.Files);
		clone.IncludeFiles.AddRange(source.IncludeFiles);
		clone.AnalysisSourceFiles.AddRange(source.AnalysisSourceFiles);
		clone.Defines.AddRange(source.Defines);
		clone.Variants.AddRange(source.Variants);
		clone.SourcefileRoots.AddRange(source.SourcefileRoots);
		clone.TestFilters.AddRange(source.TestFilters);
		clone.CoverageSubjects.AddRange(source.CoverageSubjects);
		clone.CoverageMapInputs.AddRange(source.CoverageMapInputs);
		clone.References.AddRange(source.References);
		clone.SharedLibraryApiHeaders.AddRange(source.SharedLibraryApiHeaders);
		clone.Frameworks.AddRange(source.Frameworks);
		clone.UsePackages.AddRange(source.UsePackages);
		clone.UseSourceRoots.AddRange(source.UseSourceRoots);
		return clone;
	}

	sealed class CachedProjectRequest
	{
		readonly Dictionary<string, DateTime> fileWriteTimes;

		CachedProjectRequest(CompilerRequest request, Dictionary<string, DateTime> fileWriteTimes)
		{
			Request = request;
			this.fileWriteTimes = fileWriteTimes;
		}

		public CompilerRequest Request { get; }

		public static CachedProjectRequest Create(string buildFile, CompilerRequest request)
		{
			Dictionary<string, DateTime> writeTimes = new(StringComparer.OrdinalIgnoreCase);
			AddWatchedFile(writeTimes, buildFile);
			foreach (string file in request.Files)
				AddWatchedFile(writeTimes, Path.GetFullPath(file, request.WorkingDirectory));
			foreach (string file in request.IncludeFiles)
				AddWatchedFile(writeTimes, Path.GetFullPath(file, request.WorkingDirectory));
			foreach (string file in request.AnalysisSourceFiles)
				AddWatchedFile(writeTimes, Path.GetFullPath(file, request.WorkingDirectory));
			return new CachedProjectRequest(CloneRequest(request), writeTimes);
		}

		public bool IsCurrent()
		{
			foreach ((string file, DateTime writeTime) in fileWriteTimes)
			{
				if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) != writeTime)
					return false;
			}
			return true;
		}

		static void AddWatchedFile(Dictionary<string, DateTime> writeTimes, string file)
		{
			string fullPath = Path.GetFullPath(file);
			if (File.Exists(fullPath))
				writeTimes[fullPath] = File.GetLastWriteTimeUtc(fullPath);
		}
	}

	void PublishDiagnostics(DocumentUri uri)
	{
		string path = uri.GetFileSystemPath();
		CampAnalysisSnapshot? snapshot;
		lock (gate)
			diagnosticSnapshots.TryGetValue(path, out snapshot);
		if (snapshot is not null)
			PublishDiagnostics(uri, snapshot.Diagnostics.Where(diagnostic => string.IsNullOrWhiteSpace(diagnostic.Path) || string.Equals(diagnostic.Path, path, StringComparison.OrdinalIgnoreCase)));
	}

	void PublishDiagnostics(DocumentUri uri, IEnumerable<CampSourceDiagnostic> diagnostics, bool force = false)
	{
		if (languageServer is null)
			return;
		string path = uri.GetFileSystemPath();
		List<CampSourceDiagnostic> diagnosticList = diagnostics.ToList();
		string key = CreateDiagnosticKey(diagnosticList);
		lock (gate)
		{
			if (!force && lastPublishedDiagnosticKeys.TryGetValue(path, out string? previous) && previous == key)
			{
				trace.Write("diagnostics.publish.skip", ("file", path), ("count", diagnosticList.Count), ("reason", "unchanged"));
				return;
			}
			lastPublishedDiagnosticKeys[path] = key;
		}
		trace.Write("diagnostics.publish", ("file", path), ("count", diagnosticList.Count), ("force", force));
		languageServer.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
		{
			Uri = uri,
			Diagnostics = new Container<Diagnostic>(diagnosticList.Select(CampLsp.ToLspDiagnostic))
		});
	}

	static string CreateDiagnosticKey(IReadOnlyList<CampSourceDiagnostic> diagnostics)
	{
		return string.Join('\n', diagnostics.Select(static diagnostic =>
			string.Join('|',
				diagnostic.Severity,
				diagnostic.Code ?? "",
				diagnostic.Message,
				diagnostic.Range?.Start.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
				diagnostic.Range?.Start.Character.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
				diagnostic.Range?.End.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
				diagnostic.Range?.End.Character.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "")));
	}

	sealed record OpenDocument(DocumentUri Uri, string Path, string Text, int? Version);

	sealed record CompletionTextContext(string Prefix, bool IsMember);

	sealed record QueryServiceBuild(CampAnalysisSnapshot Snapshot);

	sealed class CachedQueryService
	{
		readonly Dictionary<string, IReadOnlyList<CampDocumentSymbol>> documentSymbols = new(StringComparer.OrdinalIgnoreCase);
		readonly object gate = new();

		public CachedQueryService(CampAnalysisSnapshot snapshot)
		{
			Snapshot = snapshot;
			Service = new CampSymbolQueryService(snapshot);
		}

		public CampAnalysisSnapshot Snapshot { get; }

		public CampSymbolQueryService Service { get; }

		public IReadOnlyList<CampDocumentSymbol> GetDocumentSymbols(string path)
		{
			string fullPath = Path.GetFullPath(path);
			lock (gate)
			{
				if (!documentSymbols.TryGetValue(fullPath, out IReadOnlyList<CampDocumentSymbol>? symbols))
				{
					symbols = Service.GetDocumentSymbols(path);
					documentSymbols[fullPath] = symbols;
				}
				return symbols;
			}
		}
	}

	static double ElapsedMilliseconds(long startTimestamp)
	{
		return Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
	}
}

public sealed class CampLspTrace : IDisposable
{
	readonly object gate = new();
	readonly StreamWriter? writer;

	CampLspTrace(string? path, StreamWriter? writer)
	{
		Path = path;
		this.writer = writer;
	}

	public string? Path { get; }

	public static CampLspTrace Create()
	{
		if (string.Equals(Environment.GetEnvironmentVariable("CAMP_LSP_TRACE"), "0", StringComparison.OrdinalIgnoreCase))
			return new CampLspTrace(null, null);

		try
		{
			string directory = Environment.GetEnvironmentVariable("CAMP_LSP_TRACE_DIR") ?? DefaultTraceDirectory();
			Directory.CreateDirectory(directory);
			string filename = "camp-lsp-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Environment.ProcessId + ".jsonl";
			string path = System.IO.Path.Combine(directory, filename);
			StreamWriter writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
			{
				AutoFlush = true
			};
			return new CampLspTrace(path, writer);
		}
		catch
		{
			return new CampLspTrace(null, null);
		}
	}

	public void Write(string eventName, params (string Name, object? Value)[] fields)
	{
		if (writer is null)
			return;

		try
		{
			using MemoryStream stream = new();
			using (Utf8JsonWriter json = new(stream))
			{
				json.WriteStartObject();
				json.WriteString("ts", DateTimeOffset.UtcNow);
				json.WriteString("event", eventName);
				foreach ((string name, object? value) in fields)
					WriteProperty(json, name, value);
				json.WriteEndObject();
			}
			lock (gate)
				writer.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		lock (gate)
			writer?.Dispose();
	}

	static void WriteProperty(Utf8JsonWriter json, string name, object? value)
	{
		switch (value)
		{
			case null:
				json.WriteNull(name);
				break;
			case string text:
				json.WriteString(name, text);
				break;
			case bool boolean:
				json.WriteBoolean(name, boolean);
				break;
			case int integer:
				json.WriteNumber(name, integer);
				break;
			case long longInteger:
				json.WriteNumber(name, longInteger);
				break;
			case double number:
				json.WriteNumber(name, Math.Round(number, 3));
				break;
			default:
				json.WriteString(name, value.ToString());
				break;
		}
	}

	static string DefaultTraceDirectory()
	{
		string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(root))
			root = System.IO.Path.GetTempPath();
		return System.IO.Path.Combine(root, "Camp", "lsp-traces");
	}
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

	public static Location ToLspLocation(CampReference reference)
	{
		return new Location
		{
			Uri = DocumentUri.FromFileSystemPath(reference.Path),
			Range = ToLspRange(reference.Range)
		};
	}

	public static CompletionItem<CampCompletionIdentity> ToLspCompletionItem(CampCompletionItem item)
	{
		return new CompletionItem<CampCompletionIdentity>
		{
			Label = item.Label,
			Kind = ToLspCompletionItemKind(item.Kind),
			Detail = item.Detail,
			InsertText = item.InsertText,
			InsertTextFormat = item.IsSnippet ? InsertTextFormat.Snippet : InsertTextFormat.PlainText,
			Documentation = string.IsNullOrWhiteSpace(item.Documentation) ? null : new StringOrMarkupContent(new MarkupContent
			{
				Kind = MarkupKind.Markdown,
				Value = item.Documentation
			})
		};
	}

	public static SignatureHelp ToLspSignatureHelp(CampSignatureHelp help)
	{
		return new SignatureHelp
		{
			Signatures = new Container<SignatureInformation>(help.Signatures.Select(signature => new SignatureInformation
			{
				Label = signature.Label,
				ActiveParameter = signature == help.Signatures[help.ActiveSignature] ? help.ActiveParameter : null,
				Documentation = string.IsNullOrWhiteSpace(signature.Documentation) ? null : new StringOrMarkupContent(new MarkupContent
				{
					Kind = MarkupKind.Markdown,
					Value = signature.Documentation
				}),
				Parameters = new Container<ParameterInformation>(signature.Parameters.Select(static parameter => new ParameterInformation
				{
					Label = parameter.Label,
					Documentation = string.IsNullOrWhiteSpace(parameter.Documentation) ? null : new StringOrMarkupContent(new MarkupContent
					{
						Kind = MarkupKind.Markdown,
						Value = parameter.Documentation
					})
				}))
			})),
			ActiveSignature = help.ActiveSignature,
			ActiveParameter = help.ActiveParameter
		};
	}

	static OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind ToLspSymbolKind(CampSymbolKind kind)
	{
		return kind switch
		{
			CampSymbolKind.Type => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Class,
			CampSymbolKind.Function => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Function,
			CampSymbolKind.Method => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Method,
			CampSymbolKind.Property => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Property,
			CampSymbolKind.Component => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Field,
			CampSymbolKind.Field => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Field,
			CampSymbolKind.Variable => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Variable,
			CampSymbolKind.Parameter => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Variable,
			CampSymbolKind.EnumValue => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.EnumMember,
			CampSymbolKind.Alias => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Interface,
			_ => OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Object
		};
	}

	static CompletionItemKind ToLspCompletionItemKind(CampSymbolKind kind)
	{
		return kind switch
		{
			CampSymbolKind.Type => CompletionItemKind.Class,
			CampSymbolKind.Function => CompletionItemKind.Function,
			CampSymbolKind.Method => CompletionItemKind.Method,
			CampSymbolKind.Property => CompletionItemKind.Property,
			CampSymbolKind.Component => CompletionItemKind.Field,
			CampSymbolKind.Field => CompletionItemKind.Field,
			CampSymbolKind.Variable => CompletionItemKind.Variable,
			CampSymbolKind.Parameter => CompletionItemKind.Variable,
			CampSymbolKind.EnumValue => CompletionItemKind.EnumMember,
			CampSymbolKind.Alias => CompletionItemKind.Interface,
			CampSymbolKind.Keyword => CompletionItemKind.Keyword,
			_ => CompletionItemKind.Text
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
