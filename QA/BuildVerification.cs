using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace PropFirmGuardian.QA
{
    public static class BuildVerification
    {
        private static readonly string[] RequiredNtReferences =
        {
            "NinjaTrader.Core",
            "NinjaTrader.Gui",
            "NinjaTrader.Custom"
        };

        private static readonly string[] UnauthorizedNuGetIndicators =
        {
            "<PackageReference",
            "packages.config",
            ".nuget\\packages",
            "HintPath=\"packages\\",
            "HintPath=\"..\\packages\\"
        };

        public static string RunAllChecks()
        {
            List<CheckResult> results = new List<CheckResult>
            {
                VerifyReferences(),
                VerifyOutputPath(),
                VerifyNoNuGet(),
                VerifyObfuscationReady()
            };

            StringBuilder report = new StringBuilder();
            report.AppendLine("Prop Firm Guardian Build Verification");
            report.AppendLine("Generated: " + DateTime.Now.ToString("u"));
            report.AppendLine();

            foreach (CheckResult result in results)
            {
                report.AppendLine(string.Format("[{0}] {1}", result.Passed ? "PASS" : "FAIL", result.Name));
                report.AppendLine(result.Message);
                report.AppendLine();
            }

            return report.ToString();
        }

        public static void AssertBuildReady()
        {
            List<CheckResult> results = new List<CheckResult>
            {
                VerifyReferences(),
                VerifyOutputPath(),
                VerifyNoNuGet(),
                VerifyObfuscationReady()
            };

            List<CheckResult> failures = results.Where(result => !result.Passed).ToList();
            if (failures.Count == 0)
                return;

            StringBuilder message = new StringBuilder();
            message.AppendLine("Build verification failed:");
            foreach (CheckResult failure in failures)
                message.AppendLine(string.Format("- {0}: {1}", failure.Name, failure.Message));

            throw new InvalidOperationException(message.ToString());
        }

        public static CheckResult VerifyReferences()
        {
            XDocument project = LoadProject();
            XNamespace ns = project.Root != null && !string.IsNullOrEmpty(project.Root.Name.NamespaceName)
                ? project.Root.Name.Namespace
                : XNamespace.None;

            List<string> failures = new List<string>();

            foreach (string requiredReference in RequiredNtReferences)
            {
                XElement reference = project
                    .Descendants(ns + "Reference")
                    .FirstOrDefault(element =>
                    {
                        string include = (string)element.Attribute("Include");
                        return include != null && include.StartsWith(requiredReference, StringComparison.OrdinalIgnoreCase);
                    });

                if (reference == null)
                {
                    failures.Add(requiredReference + " reference is missing.");
                    continue;
                }

                XElement privateElement = reference.Element(ns + "Private");
                if (privateElement == null || !string.Equals(privateElement.Value.Trim(), "False", StringComparison.OrdinalIgnoreCase))
                    failures.Add(requiredReference + " must have <Private>False</Private> / Copy Local = False.");
            }

            return failures.Count == 0
                ? CheckResult.Pass("VerifyReferences", "All NT8 DLL references have Copy Local = False.")
                : CheckResult.Fail("VerifyReferences", string.Join(Environment.NewLine, failures));
        }

        public static CheckResult VerifyOutputPath()
        {
            XDocument project = LoadProject();
            XNamespace ns = project.Root != null && !string.IsNullOrEmpty(project.Root.Name.NamespaceName)
                ? project.Root.Name.Namespace
                : XNamespace.None;

            string outputPath = project
                .Descendants(ns + "OutputPath")
                .Select(element => element.Value)
                .FirstOrDefault();

            string expected = @"$(USERPROFILE)\Documents\NinjaTrader 8\bin\Custom\AddOns\";
            bool projectPathCorrect = string.Equals(outputPath, expected, StringComparison.OrdinalIgnoreCase);

            string resolvedOutput = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\AddOns\PropFirmGuardian.dll");
            bool dllExists = File.Exists(resolvedOutput);

            if (projectPathCorrect && dllExists)
                return CheckResult.Pass("VerifyOutputPath", "Output path is correct and PropFirmGuardian.dll exists in NT8 AddOns folder.");

            StringBuilder message = new StringBuilder();
            if (!projectPathCorrect)
                message.AppendLine("OutputPath mismatch. Expected: " + expected + " Actual: " + (outputPath ?? "<missing>"));

            if (!dllExists)
                message.AppendLine("Built DLL not found at: " + resolvedOutput);

            return CheckResult.Fail("VerifyOutputPath", message.ToString().Trim());
        }

        public static CheckResult VerifyNoNuGet()
        {
            string root = GetRepositoryRoot();
            List<string> failures = new List<string>();

            foreach (string filePath in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string relative = MakeRelative(root, filePath);
                if (relative.StartsWith("obj\\", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(filePath), "packages.config", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(relative);
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(filePath);
                }
                catch
                {
                    continue;
                }

                foreach (string indicator in UnauthorizedNuGetIndicators)
                {
                    if (text.IndexOf(indicator, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        failures.Add(relative + " contains " + indicator);
                        break;
                    }
                }
            }

            return failures.Count == 0
                ? CheckResult.Pass("VerifyNoNuGet", "No unauthorized NuGet package references found.")
                : CheckResult.Fail("VerifyNoNuGet", string.Join(Environment.NewLine, failures));
        }

        public static CheckResult VerifyObfuscationReady()
        {
            string root = GetRepositoryRoot();
            string viewModelsPath = Path.Combine(root, "UI", "ViewModels");
            if (!Directory.Exists(viewModelsPath))
                return CheckResult.Fail("VerifyObfuscationReady", "UI\\ViewModels folder is missing.");

            string accountVmPath = Path.Combine(viewModelsPath, "AccountViewModel.cs");
            if (!File.Exists(accountVmPath))
                return CheckResult.Fail("VerifyObfuscationReady", "AccountViewModel.cs is missing from UI\\ViewModels.");

            string markerPath = Path.Combine(viewModelsPath, "OBFUSCATION_EXCLUDE.txt");
            if (File.Exists(markerPath))
                return CheckResult.Pass("VerifyObfuscationReady", "ViewModels folder has an obfuscation exclusion marker.");

            return CheckResult.Fail(
                "VerifyObfuscationReady",
                "ViewModels folder exists, but no OBFUSCATION_EXCLUDE.txt marker was found. Exclude UI\\ViewModels from renaming in obfuscator settings.");
        }

        private static XDocument LoadProject()
        {
            return XDocument.Load(GetProjectPath());
        }

        private static string GetProjectPath()
        {
            string root = GetRepositoryRoot();
            return Path.Combine(root, "PropFirmGuardian.csproj");
        }

        private static string GetRepositoryRoot()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            DirectoryInfo directory = new DirectoryInfo(baseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PropFirmGuardian.csproj")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            return Directory.GetCurrentDirectory();
        }

        private static string MakeRelative(string root, string path)
        {
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;

            Uri rootUri = new Uri(root);
            Uri pathUri = new Uri(path);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        public sealed class CheckResult
        {
            public string Name { get; private set; }
            public bool Passed { get; private set; }
            public string Message { get; private set; }

            public static CheckResult Pass(string name, string message)
            {
                return new CheckResult
                {
                    Name = name,
                    Passed = true,
                    Message = message
                };
            }

            public static CheckResult Fail(string name, string message)
            {
                return new CheckResult
                {
                    Name = name,
                    Passed = false,
                    Message = message
                };
            }
        }
    }
}
