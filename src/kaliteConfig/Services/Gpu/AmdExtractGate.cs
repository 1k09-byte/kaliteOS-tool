
using System;

namespace kaliteConfig.Services
{
    public static class AmdExtractGate
    {
        public const int MaxAcceptableExitCode = 1;

        public static bool IsAcceptableExitCode(int exitCode) => exitCode <= MaxAcceptableExitCode;

        public const string ManifestRelativePath = @"Bin64\cccmanifest_64.json";

        public static bool IsSuccess(int exitCode, bool manifestPresent)
            => IsAcceptableExitCode(exitCode) && manifestPresent;

        public static string? FailureMessage(int exitCode, bool manifestPresent)
        {
            if (IsSuccess(exitCode, manifestPresent)) return null;

            if (!IsAcceptableExitCode(exitCode))
                return $"7-Zip could not unpack the AMD package (exit code {exitCode}).";

            return $"The AMD package unpacked, but it did not contain {ManifestRelativePath}, so there is nothing to install.";
        }

        public static bool ListsPeFormat(string sevenZipInfoOutput)
        {
            if (string.IsNullOrEmpty(sevenZipInfoOutput)) return false;

            foreach (string line in sevenZipInfoOutput.Split('\n'))
            {
                string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i + 1 < tokens.Length; i++)
                {
                    if (tokens[i] == "PE" && tokens[i + 1].StartsWith("exe", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        public static bool IsPeExecutable(ReadOnlySpan<byte> magic)
            => magic.Length >= 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';

        public static string UnexpectedPayloadMessage(long? declaredLength, string? preview)
        {
            string size = declaredLength is > 0
                ? $"{declaredLength.Value:N0} bytes"
                : "an unknown number of bytes";

            string excerpt = string.IsNullOrWhiteSpace(preview) ? string.Empty : preview.Trim();

            return $"AMD's server returned a web page ({size}{excerpt}) instead of the driver package. " +
                   "This is usually AMD's CDN refusing an automated request - try again in a few minutes, " +
                   "or download the package from amd.com and install it directly.";
        }

        public const double ExpansionRatio = 3.2;

        public const long HeadroomBytes = 512L * 1024 * 1024;

        public static long EstimateUnpackedBytes(long downloadBytes)
            => downloadBytes <= 0 ? 0 : (long)(downloadBytes * ExpansionRatio);

        public static long RequiredFreeBytes(long downloadBytes)
        {
            if (downloadBytes <= 0) return HeadroomBytes;
            return downloadBytes + EstimateUnpackedBytes(downloadBytes) + HeadroomBytes;
        }

        public static bool HasRoomFor(long freeBytes, long downloadBytes)
            => freeBytes >= RequiredFreeBytes(downloadBytes);

        public static string InsufficientSpaceMessage(long downloadBytes, long freeBytes)
        {
            long need = RequiredFreeBytes(downloadBytes);
            long shortfall = Math.Max(0, need - freeBytes);
            return $"Not enough free disk space to install this driver. The {Format(downloadBytes)} download unpacks to " +
                   $"roughly {Format(EstimateUnpackedBytes(downloadBytes))}, so about {Format(need)} has to be free at once " +
                   $"(including a {Format(HeadroomBytes)} safety margin). Only {Format(freeBytes)} is available - " +
                   $"free up {Format(shortfall)} more and press Retry.";
        }

        public enum ResumeDecision
        {
            Append,

            Replace,

            Discard,
        }

        public static ResumeDecision DecideResume(long expectedTotal, long alreadyHave, long bodyLength)
        {
            if (expectedTotal <= 0 || alreadyHave <= 0) return ResumeDecision.Replace;
            if (alreadyHave >= expectedTotal) return ResumeDecision.Discard;
            if (bodyLength == expectedTotal - alreadyHave) return ResumeDecision.Append;
            if (bodyLength == expectedTotal) return ResumeDecision.Replace;
            return ResumeDecision.Discard;
        }

        private static string Format(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
            return $"{bytes:N0} bytes";
        }
    }
}
