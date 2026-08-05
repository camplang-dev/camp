using System;
using System.Globalization;
using System.Threading;

namespace Camp.Compiler.Tests;

public static class TestResourceGate
{
	static readonly Lazy<SemaphoreSlim?> NativeSemaphore = new(() => CreateSemaphore("CAMP_TEST_NATIVE_PARALLELISM", defaultLimit: OperatingSystem.IsMacOS() ? 1 : 0));
	static readonly Lazy<SemaphoreSlim?> CliSemaphore = new(() => CreateSemaphore("CAMP_TEST_CLI_PARALLELISM", defaultLimit: OperatingSystem.IsMacOS() ? 1 : 0));

	public static IDisposable EnterNative()
	{
		return Enter(NativeSemaphore.Value);
	}

	public static IDisposable EnterCli()
	{
		return Enter(CliSemaphore.Value);
	}

	static IDisposable Enter(SemaphoreSlim? semaphore)
	{
		if (semaphore is null)
			return NoopScope.Instance;
		semaphore.Wait();
		return new SemaphoreScope(semaphore);
	}

	static SemaphoreSlim? CreateSemaphore(string environmentVariable, int defaultLimit)
	{
		int limit = defaultLimit;
		string? value = Environment.GetEnvironmentVariable(environmentVariable);
		if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
			limit = parsed;
		return limit > 0 ? new SemaphoreSlim(limit, limit) : null;
	}

	sealed class SemaphoreScope(SemaphoreSlim semaphore) : IDisposable
	{
		bool disposed;

		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;
			semaphore.Release();
		}
	}

	sealed class NoopScope : IDisposable
	{
		public static readonly NoopScope Instance = new();

		NoopScope()
		{
		}

		public void Dispose()
		{
		}
	}
}
