using System;
using System.Collections.Generic;

namespace Camp.Compiler;

public static class BuildArtifactLayout
{
	public static string GetArtifactDirectoryName(TargetDefinition target, NativeBuildKind? buildKind, string profileName)
	{
		ArgumentNullException.ThrowIfNull(target);
		string normalizedProfile = string.IsNullOrWhiteSpace(profileName) ? "DEBUG" : profileName.Trim().ToUpperInvariant();
		List<string> parts = [target.GetVariantDirectoryName()];
		if (buildKind is NativeBuildKind.Static or NativeBuildKind.Shared)
			parts.Add(buildKind == NativeBuildKind.Static ? "static" : "shared");
		parts.Add(normalizedProfile);
		return string.Join("_", parts);
	}

	public static string GetArtifactDirectoryName(TargetDefinition target, DependencyLinkKind linkKind, string profileName)
	{
		ArgumentNullException.ThrowIfNull(target);
		string normalizedProfile = string.IsNullOrWhiteSpace(profileName) ? "DEBUG" : profileName.Trim().ToUpperInvariant();
		List<string> parts = [target.GetVariantDirectoryName()];
		parts.Add(linkKind switch
		{
			DependencyLinkKind.Static => "static",
			DependencyLinkKind.Shared => "shared",
			DependencyLinkKind.Api => "api",
			_ => throw new ArgumentOutOfRangeException(nameof(linkKind), linkKind, null)
		});
		parts.Add(normalizedProfile);
		return string.Join("_", parts);
	}
}
