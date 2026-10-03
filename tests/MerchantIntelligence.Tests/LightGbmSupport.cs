using System.Runtime.InteropServices;

namespace MerchantIntelligence.Tests;

internal static class LightGbmSupport
{
    // The LightGBM native binary shipped by the Microsoft.ML.LightGbm package only
    // covers x64 runtimes (win-x64, linux-x64, osx-x64). On arm64 hosts such as
    // Apple Silicon, run `dotnet test` under an x64 runtime (Rosetta) instead.
    public static bool Supported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public const string UnsupportedReason =
        "LightGBM native binary is only shipped for x64 runtimes; run `dotnet test` under an x64 .NET runtime.";
}
