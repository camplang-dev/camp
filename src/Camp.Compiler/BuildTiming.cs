using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Camp.Compiler;

public sealed class BuildTiming
{
	readonly bool enabled;
	readonly Stack<BuildTimingNode> stack = [];
	readonly Stopwatch stopwatch = new();
	int sequence;

	BuildTiming(bool enabled, string command, string project, string target, string artifact, string profile, string compilerVersion)
	{
		this.enabled = enabled;
		StartedUtc = DateTime.UtcNow;
		Root = new BuildTimingNode("build " + project, "root", sequence++);
		Root.Metadata["command"] = command;
		Root.Metadata["project"] = project;
		Root.Metadata["target"] = target;
		Root.Metadata["artifact"] = artifact;
		Root.Metadata["profile"] = profile;
		Root.Metadata["compilerVersion"] = compilerVersion;
		if (enabled)
			stopwatch.Start();
	}

	public DateTime StartedUtc { get; }
	public BuildTimingNode Root { get; }
	public bool Enabled => enabled;

	public static BuildTiming Create(bool enabled, string command, string project, string target, string artifact, string profile, string compilerVersion)
	{
		return new BuildTiming(enabled, command, project, target, artifact, profile, compilerVersion);
	}

	public IDisposable Begin(string name, string kind, string? status = null)
	{
		return Begin(name, kind, metadata: null, status);
	}

	public IDisposable Begin(string name, string kind, IReadOnlyDictionary<string, string>? metadata, string? status = null)
	{
		if (!enabled)
			return NoopTimingScope.Instance;
		BuildTimingNode node = new(name, kind, sequence++)
		{
			Status = status
		};
		if (metadata is not null)
		{
			foreach ((string key, string value) in metadata)
				node.Metadata[key] = value;
		}
		BuildTimingNode parent = stack.Count == 0 ? Root : stack.Peek();
		parent.Children.Add(node);
		stack.Push(node);
		node.StartTicks = stopwatch.ElapsedTicks;
		return new TimingScope(this, node);
	}

	public void Mark(string name, string kind, string? status = null)
	{
		if (!enabled)
			return;
		using IDisposable _ = Begin(name, kind, status);
	}

	public void AddRootMetadata(string key, string value)
	{
		if (enabled)
			Root.Metadata[key] = value;
	}

	public void Complete(string? status = null)
	{
		if (!enabled)
			return;
		Root.Status = status ?? Root.Status;
		Root.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
		stopwatch.Stop();
	}

	public string FormatText()
	{
		if (!enabled)
			return "";
		StringBuilder builder = new();
		builder.Append("Timing: ");
		AppendNodeLine(builder, Root, depth: 0);
		foreach (BuildTimingNode child in Root.Children)
			AppendNode(builder, child, depth: 1);
		return builder.ToString();
	}

	public string FormatJson()
	{
		using MemoryStream stream = new();
		using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			writer.WriteString("startedUtc", StartedUtc);
			writer.WriteNumber("elapsedMilliseconds", Math.Round(Root.ElapsedMilliseconds, 3));
			WriteMetadata(writer, Root.Metadata);
			writer.WritePropertyName("events");
			writer.WriteStartArray();
			WriteNode(writer, Root);
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	void Finish(BuildTimingNode node)
	{
		node.ElapsedMilliseconds = Stopwatch.GetElapsedTime(node.StartTicks, stopwatch.ElapsedTicks).TotalMilliseconds;
		if (stack.Count > 0 && ReferenceEquals(stack.Peek(), node))
			stack.Pop();
		else
		{
			while (stack.Count > 0 && !ReferenceEquals(stack.Peek(), node))
				stack.Pop();
			if (stack.Count > 0)
				stack.Pop();
		}
	}

	static void AppendNode(StringBuilder builder, BuildTimingNode node, int depth)
	{
		builder.Append(' ', depth * 2);
		AppendNodeLine(builder, node, depth);
		foreach (BuildTimingNode child in node.Children)
			AppendNode(builder, child, depth + 1);
	}

	static void AppendNodeLine(StringBuilder builder, BuildTimingNode node, int depth)
	{
		_ = depth;
		builder.Append(node.Name);
		builder.Append(' ');
		builder.Append(FormatSeconds(node.ElapsedMilliseconds));
		if (!string.IsNullOrWhiteSpace(node.Status))
		{
			builder.Append(' ');
			builder.Append(node.Status);
		}
		if (node.Metadata.TryGetValue("files", out string? files))
		{
			builder.Append(' ');
			builder.Append(files);
		}
		builder.AppendLine();
	}

	static string FormatSeconds(double milliseconds)
	{
		return (milliseconds / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + "s";
	}

	static void WriteNode(Utf8JsonWriter writer, BuildTimingNode node)
	{
		writer.WriteStartObject();
		writer.WriteString("name", node.Name);
		writer.WriteString("kind", node.Kind);
		if (!string.IsNullOrWhiteSpace(node.Status))
			writer.WriteString("status", node.Status);
		writer.WriteNumber("elapsedMilliseconds", Math.Round(node.ElapsedMilliseconds, 3));
		if (node.Metadata.Count > 0)
			WriteMetadata(writer, node.Metadata);
		writer.WritePropertyName("children");
		writer.WriteStartArray();
		foreach (BuildTimingNode child in node.Children)
			WriteNode(writer, child);
		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	static void WriteMetadata(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> metadata)
	{
		writer.WritePropertyName("metadata");
		writer.WriteStartObject();
		foreach ((string key, string value) in metadata)
			writer.WriteString(key, value);
		writer.WriteEndObject();
	}

	sealed class TimingScope(BuildTiming timing, BuildTimingNode node) : IDisposable
	{
		public void Dispose()
		{
			timing.Finish(node);
		}
	}

	sealed class NoopTimingScope : IDisposable
	{
		public static readonly NoopTimingScope Instance = new();
		public void Dispose()
		{
		}
	}
}

public sealed class BuildTimingNode(string name, string kind, int sequence)
{
	public string Name { get; } = name;
	public string Kind { get; } = kind;
	public int Sequence { get; } = sequence;
	public string? Status { get; set; }
	public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);
	public List<BuildTimingNode> Children { get; } = [];
	public long StartTicks { get; set; }
	public double ElapsedMilliseconds { get; set; }
}
