﻿// Copyright (c) Tunnel Vision Laboratories, LLC. All Rights Reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#pragma warning disable SA1202 // Elements should be ordered by access
#pragma warning disable SA1204 // Static elements should appear before instance elements

namespace StyleCop.Analyzers.Test.Verifiers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Formatting;
    using Microsoft.CodeAnalysis.Host.Mef;
    using Microsoft.CodeAnalysis.Options;
    using Microsoft.CodeAnalysis.Simplification;
    using Microsoft.CodeAnalysis.Text;
    using Microsoft.CodeAnalysis.VisualBasic;
    using Microsoft.VisualStudio.Composition;
    using StyleCop.Analyzers.Test.Helpers;
    using TestHelper;
    using Xunit;
    using Path = System.IO.Path;

    internal abstract class GenericAnalyzerTest
    {
        private const int DefaultNumberOfIncrementalIterations = -1000;

        private static readonly string DefaultFilePathPrefix = "Test";
        private static readonly string CSharpDefaultFileExt = "cs";
        private static readonly string VisualBasicDefaultExt = "vb";
        private static readonly string CSharpDefaultFilePath = DefaultFilePathPrefix + 0 + "." + CSharpDefaultFileExt;
        private static readonly string VisualBasicDefaultFilePath = DefaultFilePathPrefix + 0 + "." + VisualBasicDefaultExt;
        private static readonly string TestProjectName = "TestProject";

        private static readonly Lazy<IExportProviderFactory> ExportProviderFactory;

        static GenericAnalyzerTest()
        {
            ExportProviderFactory = new Lazy<IExportProviderFactory>(
                () =>
                {
                    var discovery = new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true);
                    var parts = Task.Run(() => discovery.CreatePartsAsync(MefHostServices.DefaultAssemblies)).GetAwaiter().GetResult();
                    var catalog = ComposableCatalog.Create(Resolver.DefaultInstance).AddParts(parts);

                    var configuration = CompositionConfiguration.Create(catalog);
                    var runtimeComposition = RuntimeComposition.CreateRuntimeComposition(configuration);
                    return runtimeComposition.CreateExportProviderFactory();
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        protected GenericAnalyzerTest()
        {
            this.TestSources = new SourceFileList(DefaultFilePathPrefix, this.Language == LanguageNames.CSharp ? CSharpDefaultFileExt : VisualBasicDefaultExt);
            this.FixedSources = new SourceFileList(DefaultFilePathPrefix, this.Language == LanguageNames.CSharp ? CSharpDefaultFileExt : VisualBasicDefaultExt);
            this.BatchFixedSources = new SourceFileList(DefaultFilePathPrefix, this.Language == LanguageNames.CSharp ? CSharpDefaultFileExt : VisualBasicDefaultExt);
        }

        /// <summary>
        /// Gets the language name used for the test.
        /// </summary>
        /// <value>
        /// The language name used for the test.
        /// </value>
        public abstract string Language
        {
            get;
        }

        public string TestCode
        {
            set
            {
                Assert.Empty(this.TestSources);
                if (value != null)
                {
                    this.TestSources.Add(value);
                }
            }
        }

        public SourceFileList TestSources { get; }

        public SourceFileCollection AdditionalFiles { get; } = new SourceFileCollection();

        public SourceFileCollection FixedAdditionalFiles { get; } = new SourceFileCollection();

        public Dictionary<string, string> XmlReferences { get; } = new Dictionary<string, string>();

        public List<DiagnosticResult> ExpectedDiagnostics { get; } = new List<DiagnosticResult>();

        public List<DiagnosticResult> RemainingDiagnostics { get; } = new List<DiagnosticResult>();

        public List<DiagnosticResult> BatchRemainingDiagnostics { get; } = new List<DiagnosticResult>();

        public bool VerifyExclusions { get; set; }

        public List<string> DisabledDiagnostics { get; } = new List<string>();

        public int? CodeFixIndex { get; set; }

        public string FixedCode
        {
            set
            {
                Assert.Empty(this.FixedSources);
                if (value != null)
                {
                    this.FixedSources.Add(value);
                }
            }
        }

        public SourceFileList FixedSources { get; }

        public string BatchFixedCode
        {
            set
            {
                Assert.Empty(this.BatchFixedSources);
                if (value != null)
                {
                    this.BatchFixedSources.Add(value);
                }
            }
        }

        public SourceFileList BatchFixedSources { get; }

        public int? NumberOfIncrementalIterations { get; set; }

        public int? NumberOfFixAllIterations { get; set; }

        public int? NumberOfFixAllInDocumentIterations { get; set; }

        public bool AllowNewCompilerDiagnostics { get; set; } = false;

        public List<Func<OptionSet, OptionSet>> OptionsTransforms { get; } = new List<Func<OptionSet, OptionSet>>();

        public List<Func<Solution, ProjectId, Solution>> SolutionTransforms { get; } = new List<Func<Solution, ProjectId, Solution>>();

        public List<Func<IEnumerable<(string filename, SourceText content)>>> AdditionalFilesFactories { get; } = new List<Func<IEnumerable<(string filename, SourceText content)>>>();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Assert.NotEmpty(this.TestSources);

            var expected = this.ExpectedDiagnostics.ToArray();
            (string filename, SourceText content)[] additionalFiles = this.AdditionalFiles.Concat(this.AdditionalFilesFactories.SelectMany(factory => factory())).ToArray();
            await this.VerifyDiagnosticsAsync(this.TestSources.ToArray(), additionalFiles, expected, cancellationToken).ConfigureAwait(false);
            if (this.HasFixableDiagnostics())
            {
                var fixedAdditionalFiles = this.FixedAdditionalFiles.Count == 0 ? additionalFiles : this.FixedAdditionalFiles.Concat(this.AdditionalFilesFactories.SelectMany(factory => factory())).ToArray();
                var remainingDiagnostics = this.FixedSources.SequenceEqual(this.TestSources, SourceFileEqualityComparer.Instance) && fixedAdditionalFiles.SequenceEqual(additionalFiles) ? expected : this.RemainingDiagnostics.ToArray();
                await this.VerifyDiagnosticsAsync(this.FixedSources.ToArray(), fixedAdditionalFiles.ToArray(), remainingDiagnostics, cancellationToken).ConfigureAwait(false);
                if (this.BatchFixedSources.Any())
                {
                    var batchRemainingDiagnostics = this.BatchFixedSources.SequenceEqual(this.TestSources, SourceFileEqualityComparer.Instance) && fixedAdditionalFiles.SequenceEqual(additionalFiles) ? expected : this.BatchRemainingDiagnostics.ToArray();
                    await this.VerifyDiagnosticsAsync(this.BatchFixedSources.ToArray(), fixedAdditionalFiles.ToArray(), batchRemainingDiagnostics, cancellationToken).ConfigureAwait(false);
                }

                await this.VerifyFixAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Gets the analyzers being tested.
        /// </summary>
        /// <returns>
        /// New instances of all the analyzers being tested.
        /// </returns>
        protected abstract IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers();

        /// <summary>
        /// Returns the code fixes being tested - to be implemented in non-abstract class.
        /// </summary>
        /// <returns>The <see cref="CodeFixProvider"/> to be used for C# code.</returns>
        protected abstract IEnumerable<CodeFixProvider> GetCodeFixProviders();

        private bool HasFixableDiagnostics()
        {
            var fixers = this.GetCodeFixProviders().ToArray();
            if (HasFixableDiagnosticsCore())
            {
                if (this.FixedSources.Count > 0)
                {
                    return true;
                }

                Assert.Empty(this.RemainingDiagnostics);
                Assert.Empty(this.BatchRemainingDiagnostics);
                return false;
            }

            Assert.True(this.FixedSources.Count == 0
                || (this.FixedSources.Count == 1 && IsNullOrEmpty(this.FixedSources[0].content))
                || this.FixedSources.SequenceEqual(this.TestSources, SourceFileEqualityComparer.Instance));
            Assert.True(this.BatchFixedSources.Count == 0
                || (this.BatchFixedSources.Count == 1 && IsNullOrEmpty(this.BatchFixedSources[0].content))
                || this.BatchFixedSources.SequenceEqual(this.TestSources, SourceFileEqualityComparer.Instance));
            Assert.Empty(this.RemainingDiagnostics);
            Assert.Empty(this.BatchRemainingDiagnostics);
            return false;

            // Local functions
            bool HasFixableDiagnosticsCore()
            {
                return this.ExpectedDiagnostics.Any(diagnostic => fixers.Any(fixer => fixer.FixableDiagnosticIds.Contains(diagnostic.Id)));
            }

            bool IsNullOrEmpty(SourceText content)
            {
                return content is null || content.Length == 0;
            }
        }

        private static bool IsSubjectToExclusion(DiagnosticResult result)
        {
            if (result.Id.StartsWith("CS", StringComparison.Ordinal))
            {
                return false;
            }

            if (result.Id.StartsWith("AD", StringComparison.Ordinal))
            {
                return false;
            }

            if (result.Spans.Length == 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// General method that gets a collection of actual <see cref="Diagnostic"/>s found in the source after the
        /// analyzer is run, then verifies each of them.
        /// </summary>
        /// <param name="sources">An array of strings to create source documents from to run the analyzers on.</param>
        /// <param name="expected">A collection of <see cref="DiagnosticResult"/>s that should appear after the analyzer
        /// is run on the sources.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task VerifyDiagnosticsAsync((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, DiagnosticResult[] expected, CancellationToken cancellationToken)
        {
            var analyzers = this.GetDiagnosticAnalyzers().ToImmutableArray();
            VerifyDiagnosticResults(await this.GetSortedDiagnosticsAsync(sources, additionalFiles, analyzers, cancellationToken).ConfigureAwait(false), analyzers, expected);

            // Automatically test for exclusions
            if (this.VerifyExclusions)
            {
                // Also check if the analyzer honors exclusions
                if (expected.Any(IsSubjectToExclusion))
                {
                    // Diagnostics reported by the compiler and analyzer diagnostics which don't have a location will
                    // still be reported. We also insert a new line at the beginning so we have to move all diagnostic
                    // locations which have a specific position down by one line.
                    var expectedResults = expected
                        .Where(x => !IsSubjectToExclusion(x))
                        .Select(x => x.WithLineOffset(1))
                        .ToArray();

                    VerifyDiagnosticResults(await this.GetSortedDiagnosticsAsync(sources.Select(x => (x.filename, x.content.Replace(new TextSpan(0, 0), " // <auto-generated>\r\n"))).ToArray(), additionalFiles, analyzers, cancellationToken).ConfigureAwait(false), analyzers, expectedResults);
                }
            }
        }

        /// <summary>
        /// Given an analyzer and a collection of documents to apply it to, run the analyzer and gather an array of
        /// diagnostics found. The returned diagnostics are then ordered by location in the source documents.
        /// </summary>
        /// <param name="analyzers">The analyzer to run on the documents.</param>
        /// <param name="documents">The <see cref="Document"/>s that the analyzer will be run on.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A collection of <see cref="Diagnostic"/>s that surfaced in the source code, sorted by
        /// <see cref="Diagnostic.Location"/>.</returns>
        protected static async Task<ImmutableArray<Diagnostic>> GetSortedDiagnosticsFromDocumentsAsync(ImmutableArray<DiagnosticAnalyzer> analyzers, Document[] documents, CancellationToken cancellationToken)
        {
            var projects = new HashSet<Project>();
            foreach (var document in documents)
            {
                projects.Add(document.Project);
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var project in projects)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions, cancellationToken);
                var compilerDiagnostics = compilation.GetDiagnostics(cancellationToken);
                var compilerErrors = compilerDiagnostics.Where(i => i.Severity == DiagnosticSeverity.Error);
                var diags = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
                var allDiagnostics = await compilationWithAnalyzers.GetAllDiagnosticsAsync().ConfigureAwait(false);
                var failureDiagnostics = allDiagnostics.Where(diagnostic => diagnostic.Id == "AD0001");
                foreach (var diag in diags.Concat(compilerErrors).Concat(failureDiagnostics))
                {
                    if (diag.Location == Location.None || diag.Location.IsInMetadata)
                    {
                        diagnostics.Add(diag);
                    }
                    else
                    {
                        for (int i = 0; i < documents.Length; i++)
                        {
                            var document = documents[i];
                            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                            if (tree == diag.Location.SourceTree)
                            {
                                diagnostics.Add(diag);
                            }
                        }
                    }
                }
            }

            var results = SortDistinctDiagnostics(diagnostics);
            return results.ToImmutableArray();
        }

        /// <summary>
        /// Sort <see cref="Diagnostic"/>s by location in source document.
        /// </summary>
        /// <param name="diagnostics">A collection of <see cref="Diagnostic"/>s to be sorted.</param>
        /// <returns>A collection containing the input <paramref name="diagnostics"/>, sorted by
        /// <see cref="Diagnostic.Location"/> and <see cref="Diagnostic.Id"/>.</returns>
        private static Diagnostic[] SortDistinctDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id).ToArray();
        }

        /// <summary>
        /// Given classes in the form of strings, their language, and an <see cref="DiagnosticAnalyzer"/> to apply to
        /// it, return the <see cref="Diagnostic"/>s found in the string after converting it to a
        /// <see cref="Document"/>.
        /// </summary>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <param name="analyzers">The analyzers to be run on the sources.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A collection of <see cref="Diagnostic"/>s that surfaced in the source code, sorted by
        /// <see cref="Diagnostic.Location"/>.</returns>
        private Task<ImmutableArray<Diagnostic>> GetSortedDiagnosticsAsync((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, ImmutableArray<DiagnosticAnalyzer> analyzers, CancellationToken cancellationToken)
        {
            return GetSortedDiagnosticsFromDocumentsAsync(analyzers, this.GetDocuments(sources, additionalFiles), cancellationToken);
        }

        /// <summary>
        /// Given an array of strings as sources and a language, turn them into a <see cref="Project"/> and return the
        /// documents and spans of it.
        /// </summary>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <returns>A collection of <see cref="Document"/>s representing the sources.</returns>
        private Document[] GetDocuments((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles)
        {
            if (this.Language != LanguageNames.CSharp && this.Language != LanguageNames.VisualBasic)
            {
                throw new ArgumentException("Unsupported Language");
            }

            var project = this.CreateProject(sources, additionalFiles, this.Language);
            var documents = project.Documents.ToArray();

            if (sources.Length != documents.Length)
            {
                throw new SystemException("Amount of sources did not match amount of Documents created");
            }

            return documents;
        }

        /// <summary>
        /// Create a project using the input strings as sources.
        /// </summary>
        /// <remarks>
        /// <para>This method first creates a <see cref="Project"/> by calling <see cref="CreateProjectImpl"/>, and then
        /// applies compilation options to the project by calling <see cref="ApplyCompilationOptions"/>.</para>
        /// </remarks>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <param name="additionalFiles">Additional documents to include in the project.</param>
        /// <param name="language">The language the source classes are in. Values may be taken from the
        /// <see cref="LanguageNames"/> class.</param>
        /// <returns>A <see cref="Project"/> created out of the <see cref="Document"/>s created from the source
        /// strings.</returns>
        protected Project CreateProject((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, string language)
        {
            Project project = this.CreateProjectImpl(sources, additionalFiles, language);
            return this.ApplyCompilationOptions(project);
        }

        /// <summary>
        /// Create a project using the input strings as sources.
        /// </summary>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <param name="additionalFiles">Additional documents to include in the project.</param>
        /// <param name="language">The language the source classes are in. Values may be taken from the
        /// <see cref="LanguageNames"/> class.</param>
        /// <returns>A <see cref="Project"/> created out of the <see cref="Document"/>s created from the source
        /// strings.</returns>
        protected virtual Project CreateProjectImpl((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, string language)
        {
            var projectId = ProjectId.CreateNewId(debugName: TestProjectName);
            var solution = this.CreateSolution(projectId, language);

            for (int i = 0; i < sources.Length; i++)
            {
                var (newFileName, source) = sources[i];
                var documentId = DocumentId.CreateNewId(projectId, debugName: newFileName);
                solution = solution.AddDocument(documentId, newFileName, source);
            }

            for (int i = 0; i < additionalFiles.Length; i++)
            {
                var (newFileName, source) = additionalFiles[i];
                var documentId = DocumentId.CreateNewId(projectId, debugName: newFileName);
                solution = solution.AddAdditionalDocument(documentId, newFileName, source);
            }

            foreach (var transform in this.SolutionTransforms)
            {
                solution = transform(solution, projectId);
            }

            return solution.GetProject(projectId);
        }

        /// <summary>
        /// Applies compilation options to a project.
        /// </summary>
        /// <remarks>
        /// <para>The default implementation configures the project by enabling all supported diagnostics of analyzers
        /// included in <see cref="GetDiagnosticAnalyzers"/> as well as <c>AD0001</c>. After configuring these
        /// diagnostics, any diagnostic IDs indicated in <see cref="DisabledDiagnostics"/> are explicitly suppressed
        /// using <see cref="ReportDiagnostic.Suppress"/>.</para>
        /// </remarks>
        /// <param name="project">The project.</param>
        /// <returns>The modified project.</returns>
        protected virtual Project ApplyCompilationOptions(Project project)
        {
            var analyzers = this.GetDiagnosticAnalyzers();

            var supportedDiagnosticsSpecificOptions = new Dictionary<string, ReportDiagnostic>();
            foreach (var analyzer in analyzers)
            {
                foreach (var diagnostic in analyzer.SupportedDiagnostics)
                {
                    // make sure the analyzers we are testing are enabled
                    supportedDiagnosticsSpecificOptions[diagnostic.Id] = ReportDiagnostic.Default;
                }
            }

            // Report exceptions during the analysis process as errors
            supportedDiagnosticsSpecificOptions.Add("AD0001", ReportDiagnostic.Error);

            foreach (var id in this.DisabledDiagnostics)
            {
                supportedDiagnosticsSpecificOptions[id] = ReportDiagnostic.Suppress;
            }

            // update the project compilation options
            var modifiedSpecificDiagnosticOptions = supportedDiagnosticsSpecificOptions.ToImmutableDictionary().SetItems(project.CompilationOptions.SpecificDiagnosticOptions);
            var modifiedCompilationOptions = project.CompilationOptions.WithSpecificDiagnosticOptions(modifiedSpecificDiagnosticOptions);

            Solution solution = project.Solution.WithProjectCompilationOptions(project.Id, modifiedCompilationOptions);
            return solution.GetProject(project.Id);
        }

        internal static AdhocWorkspace CreateWorkspace()
        {
            var exportProvider = ExportProviderFactory.Value.CreateExportProvider();
            var host = MefV1HostServices.Create(exportProvider.AsExportProvider());
            return new AdhocWorkspace(host);
        }

        /// <summary>
        /// Creates a solution that will be used as parent for the sources that need to be checked.
        /// </summary>
        /// <param name="projectId">The project identifier to use.</param>
        /// <param name="language">The language for which the solution is being created.</param>
        /// <returns>The created solution.</returns>
        protected virtual Solution CreateSolution(ProjectId projectId, string language)
        {
            var compilationOptions = language == LanguageNames.CSharp
                ? (CompilationOptions)new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
                : new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            var xmlReferenceResolver = new TestXmlReferenceResolver();
            foreach (var xmlReference in this.XmlReferences)
            {
                xmlReferenceResolver.XmlReferences.Add(xmlReference.Key, xmlReference.Value);
            }

            compilationOptions = compilationOptions.WithXmlReferenceResolver(xmlReferenceResolver);

            Solution solution = CreateWorkspace()
                .CurrentSolution
                .AddProject(projectId, TestProjectName, TestProjectName, language)
                .WithProjectCompilationOptions(projectId, compilationOptions)
                .AddMetadataReference(projectId, MetadataReferences.CorlibReference)
                .AddMetadataReference(projectId, MetadataReferences.SystemReference)
                .AddMetadataReference(projectId, MetadataReferences.SystemCoreReference)
                .AddMetadataReference(projectId, MetadataReferences.CSharpSymbolsReference)
                .AddMetadataReference(projectId, MetadataReferences.CodeAnalysisReference);

            if (MetadataReferences.SystemRuntimeReference != null)
            {
                solution = solution.AddMetadataReference(projectId, MetadataReferences.SystemRuntimeReference);
            }

            if (MetadataReferences.SystemValueTupleReference != null)
            {
                solution = solution.AddMetadataReference(projectId, MetadataReferences.SystemValueTupleReference);
            }

            foreach (var transform in this.OptionsTransforms)
            {
                solution.Workspace.Options = transform(solution.Workspace.Options);
            }

            ParseOptions parseOptions = solution.GetProject(projectId).ParseOptions;
            solution = solution.WithProjectParseOptions(projectId, parseOptions.WithDocumentationMode(DocumentationMode.Diagnose));

            return solution;
        }

        /// <summary>
        /// Checks each of the actual <see cref="Diagnostic"/>s found and compares them with the corresponding
        /// <see cref="DiagnosticResult"/> in the array of expected results. <see cref="Diagnostic"/>s are considered
        /// equal only if the <see cref="DiagnosticResult.Spans"/>, <see cref="DiagnosticResult.Id"/>,
        /// <see cref="DiagnosticResult.Severity"/>, and <see cref="DiagnosticResult.Message"/> of the
        /// <see cref="DiagnosticResult"/> match the actual <see cref="Diagnostic"/>.
        /// </summary>
        /// <param name="actualResults">The <see cref="Diagnostic"/>s found by the compiler after running the analyzer
        /// on the source code.</param>
        /// <param name="analyzers">The analyzers that have been run on the sources.</param>
        /// <param name="expectedResults">A collection of <see cref="DiagnosticResult"/>s describing the expected
        /// diagnostics for the sources.</param>
        private static void VerifyDiagnosticResults(IEnumerable<Diagnostic> actualResults, ImmutableArray<DiagnosticAnalyzer> analyzers, DiagnosticResult[] expectedResults)
        {
            int expectedCount = expectedResults.Length;
            int actualCount = actualResults.Count();

            if (expectedCount != actualCount)
            {
                string diagnosticsOutput = actualResults.Any() ? FormatDiagnostics(analyzers, actualResults.ToArray()) : "    NONE.";

                Assert.True(
                    false,
                    string.Format("Mismatch between number of diagnostics returned, expected \"{0}\" actual \"{1}\"\r\n\r\nDiagnostics:\r\n{2}\r\n", expectedCount, actualCount, diagnosticsOutput));
            }

            for (int i = 0; i < expectedResults.Length; i++)
            {
                var actual = actualResults.ElementAt(i);
                var expected = expectedResults[i];

                if (!expected.HasLocation)
                {
                    if (actual.Location != Location.None)
                    {
                        string message =
                            string.Format(
                                "Expected:\nA project diagnostic with No location\nActual:\n{0}",
                                FormatDiagnostics(analyzers, actual));
                        Assert.True(false, message);
                    }
                }
                else
                {
                    VerifyDiagnosticLocation(analyzers, actual, actual.Location, expected.Spans.First());
                    var additionalLocations = actual.AdditionalLocations.ToArray();

                    if (additionalLocations.Length != expected.Spans.Length - 1)
                    {
                        Assert.True(
                            false,
                            string.Format(
                                "Expected {0} additional locations but got {1} for Diagnostic:\r\n    {2}\r\n",
                                expected.Spans.Length - 1,
                                additionalLocations.Length,
                                FormatDiagnostics(analyzers, actual)));
                    }

                    for (int j = 0; j < additionalLocations.Length; ++j)
                    {
                        VerifyDiagnosticLocation(analyzers, actual, additionalLocations[j], expected.Spans[j + 1]);
                    }
                }

                if (actual.Id != expected.Id)
                {
                    string message =
                        string.Format(
                            "Expected diagnostic id to be \"{0}\" was \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Id,
                            actual.Id,
                            FormatDiagnostics(analyzers, actual));
                    Assert.True(false, message);
                }

                if (actual.Severity != expected.Severity)
                {
                    string message =
                        string.Format(
                            "Expected diagnostic severity to be \"{0}\" was \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Severity,
                            actual.Severity,
                            FormatDiagnostics(analyzers, actual));
                    Assert.True(false, message);
                }

                if (actual.GetMessage() != expected.Message)
                {
                    string message =
                        string.Format(
                            "Expected diagnostic message to be \"{0}\" was \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Message,
                            actual.GetMessage(),
                            FormatDiagnostics(analyzers, actual));
                    Assert.True(false, message);
                }
            }
        }

        /// <summary>
        /// Helper method to <see cref="VerifyDiagnosticResults"/> that checks the location of a
        /// <see cref="Diagnostic"/> and compares it with the location described by a
        /// <see cref="FileLinePositionSpan"/>.
        /// </summary>
        /// <param name="analyzers">The analyzer that have been run on the sources.</param>
        /// <param name="diagnostic">The diagnostic that was found in the code.</param>
        /// <param name="actual">The location of the diagnostic found in the code.</param>
        /// <param name="expected">The <see cref="FileLinePositionSpan"/> describing the expected location of the
        /// diagnostic.</param>
        private static void VerifyDiagnosticLocation(ImmutableArray<DiagnosticAnalyzer> analyzers, Diagnostic diagnostic, Location actual, FileLinePositionSpan expected)
        {
            var actualSpan = actual.GetLineSpan();

            string message =
                string.Format(
                    "Expected diagnostic to be in file \"{0}\" was actually in file \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                    expected.Path,
                    actualSpan.Path,
                    FormatDiagnostics(analyzers, diagnostic));
            Assert.True(
                actualSpan.Path == expected.Path || (actualSpan.Path != null && actualSpan.Path.Contains("Test0.") && expected.Path.Contains("Test.")),
                message);

            var actualStartLinePosition = actualSpan.StartLinePosition;
            var actualEndLinePosition = actualSpan.EndLinePosition;

            VerifyLinePosition(analyzers, diagnostic, actualSpan.StartLinePosition, expected.StartLinePosition, "start");
            if (expected.StartLinePosition < expected.EndLinePosition)
            {
                VerifyLinePosition(analyzers, diagnostic, actualSpan.EndLinePosition, expected.EndLinePosition, "end");
            }
        }

        private static void VerifyLinePosition(ImmutableArray<DiagnosticAnalyzer> analyzers, Diagnostic diagnostic, LinePosition actualLinePosition, LinePosition expectedLinePosition, string positionText)
        {
            // Only check the line position if it matters
            if (expectedLinePosition.Line > 0)
            {
                Assert.True(
                    (actualLinePosition.Line + 1) == expectedLinePosition.Line,
                    string.Format(
                        "Expected diagnostic to {0} on line \"{1}\" was actually on line \"{2}\"\r\n\r\nDiagnostic:\r\n    {3}\r\n",
                        positionText,
                        expectedLinePosition.Line,
                        actualLinePosition.Line + 1,
                        FormatDiagnostics(analyzers, diagnostic)));
            }

            // Only check the column position if it matters
            if (expectedLinePosition.Character > 0)
            {
                Assert.True(
                    (actualLinePosition.Character + 1) == expectedLinePosition.Character,
                    string.Format(
                        "Expected diagnostic to {0} at column \"{1}\" was actually at column \"{2}\"\r\n\r\nDiagnostic:\r\n    {3}\r\n",
                        positionText,
                        expectedLinePosition.Character,
                        actualLinePosition.Character + 1,
                        FormatDiagnostics(analyzers, diagnostic)));
            }
        }

        /// <summary>
        /// Helper method to format a <see cref="Diagnostic"/> into an easily readable string.
        /// </summary>
        /// <param name="analyzers">The analyzers that this verifier tests.</param>
        /// <param name="diagnostics">A collection of <see cref="Diagnostic"/>s to be formatted.</param>
        /// <returns>The <paramref name="diagnostics"/> formatted as a string.</returns>
        private static string FormatDiagnostics(ImmutableArray<DiagnosticAnalyzer> analyzers, params Diagnostic[] diagnostics)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < diagnostics.Length; ++i)
            {
                var diagnosticsId = diagnostics[i].Id;

                builder.Append("// ").AppendLine(diagnostics[i].ToString());

                var applicableAnalyzer = analyzers.FirstOrDefault(a => a.SupportedDiagnostics.Any(dd => dd.Id == diagnosticsId));
                if (applicableAnalyzer != null)
                {
                    var analyzerType = applicableAnalyzer.GetType();

                    var location = diagnostics[i].Location;
                    if (location == Location.None)
                    {
                        builder.AppendFormat("GetGlobalResult({0}.{1})", analyzerType.Name, diagnosticsId);
                    }
                    else
                    {
                        Assert.True(
                            location.IsInSource,
                            string.Format("Test base does not currently handle diagnostics in metadata locations. Diagnostic in metadata:\r\n{0}", diagnostics[i]));

                        string resultMethodName = diagnostics[i].Location.SourceTree.FilePath.EndsWith(".cs") ? "GetCSharpResultAt" : "GetBasicResultAt";
                        var linePosition = diagnostics[i].Location.GetLineSpan().StartLinePosition;

                        builder.AppendFormat(
                            "{0}({1}, {2}, {3}.{4})",
                            resultMethodName,
                            linePosition.Line + 1,
                            linePosition.Character + 1,
                            analyzerType.Name,
                            diagnosticsId);
                    }

                    if (i != diagnostics.Length - 1)
                    {
                        builder.Append(',');
                    }

                    builder.AppendLine();
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Called to test a C# code fix when applied on the input source as a string.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        protected async Task VerifyFixAsync(CancellationToken cancellationToken)
        {
            var oldSources = this.TestSources.ToArray();
            var additionalFiles = this.AdditionalFiles.ToArray();
            var newSources = this.FixedSources.ToArray();
            var fixedAdditionalFiles = this.FixedAdditionalFiles.Any() ? this.FixedAdditionalFiles.ToArray() : this.AdditionalFiles.ToArray();
            var batchNewSources = this.BatchFixedSources.Any() ? this.BatchFixedSources.ToArray() : newSources;

            int numberOfIncrementalIterations;
            int numberOfFixAllIterations;
            if (this.NumberOfIncrementalIterations != null)
            {
                numberOfIncrementalIterations = this.NumberOfIncrementalIterations.Value;
            }
            else
            {
                if (!HasAnyChange(oldSources, newSources) && !HasAnyChange(additionalFiles, fixedAdditionalFiles))
                {
                    numberOfIncrementalIterations = 0;
                }
                else
                {
                    numberOfIncrementalIterations = DefaultNumberOfIncrementalIterations;
                }
            }

            if (this.NumberOfFixAllIterations != null)
            {
                numberOfFixAllIterations = this.NumberOfFixAllIterations.Value;
            }
            else
            {
                if (!HasAnyChange(oldSources, batchNewSources) && !HasAnyChange(additionalFiles, fixedAdditionalFiles))
                {
                    numberOfFixAllIterations = 0;
                }
                else
                {
                    numberOfFixAllIterations = 1;
                }
            }

            int numberOfFixAllInDocumentIterations = this.NumberOfFixAllInDocumentIterations ?? numberOfFixAllIterations;

            var t1 = this.VerifyFixInternalAsync(this.Language, this.GetDiagnosticAnalyzers().ToImmutableArray(), this.GetCodeFixProviders().ToImmutableArray(), oldSources, additionalFiles, newSources, fixedAdditionalFiles, numberOfIncrementalIterations, FixEachAnalyzerDiagnosticAsync, cancellationToken).ConfigureAwait(false);

            var fixAllProvider = this.GetCodeFixProviders().Select(codeFixProvider => codeFixProvider.GetFixAllProvider()).Where(codeFixProvider => codeFixProvider != null).ToImmutableArray();

            if (fixAllProvider.IsEmpty)
            {
                await t1;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    await t1;
                }

                var t2 = this.VerifyFixInternalAsync(LanguageNames.CSharp, this.GetDiagnosticAnalyzers().ToImmutableArray(), this.GetCodeFixProviders().ToImmutableArray(), oldSources, additionalFiles, batchNewSources ?? newSources, fixedAdditionalFiles, numberOfFixAllInDocumentIterations, FixAllAnalyzerDiagnosticsInDocumentAsync, cancellationToken).ConfigureAwait(false);
                if (Debugger.IsAttached)
                {
                    await t2;
                }

                var t3 = this.VerifyFixInternalAsync(LanguageNames.CSharp, this.GetDiagnosticAnalyzers().ToImmutableArray(), this.GetCodeFixProviders().ToImmutableArray(), oldSources, additionalFiles, batchNewSources ?? newSources, fixedAdditionalFiles, numberOfFixAllIterations, FixAllAnalyzerDiagnosticsInProjectAsync, cancellationToken).ConfigureAwait(false);
                if (Debugger.IsAttached)
                {
                    await t3;
                }

                var t4 = this.VerifyFixInternalAsync(LanguageNames.CSharp, this.GetDiagnosticAnalyzers().ToImmutableArray(), this.GetCodeFixProviders().ToImmutableArray(), oldSources, additionalFiles, batchNewSources ?? newSources, fixedAdditionalFiles, numberOfFixAllIterations, FixAllAnalyzerDiagnosticsInSolutionAsync, cancellationToken).ConfigureAwait(false);
                if (Debugger.IsAttached)
                {
                    await t4;
                }

                if (!Debugger.IsAttached)
                {
                    // Allow the operations to run in parallel
                    await t1;
                    await t2;
                    await t3;
                    await t4;
                }
            }
        }

        private async Task VerifyFixInternalAsync(
            string language,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            ImmutableArray<CodeFixProvider> codeFixProviders,
            (string filename, SourceText content)[] oldSources,
            (string filename, SourceText content)[] additionalFiles,
            (string filename, SourceText content)[] newSources,
            (string filename, SourceText content)[] fixedAdditionalFiles,
            int numberOfIterations,
            Func<ImmutableArray<DiagnosticAnalyzer>, ImmutableArray<CodeFixProvider>, int?, Project, int, CancellationToken, Task<Project>> getFixedProject,
            CancellationToken cancellationToken)
        {
            additionalFiles = additionalFiles.Concat(this.AdditionalFilesFactories.SelectMany(factory => factory())).ToArray();
            fixedAdditionalFiles = fixedAdditionalFiles.Concat(this.AdditionalFilesFactories.SelectMany(factory => factory())).ToArray();

            var project = this.CreateProject(oldSources, additionalFiles, language);
            var compilerDiagnostics = await GetCompilerDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);

            project = await getFixedProject(analyzers, codeFixProviders, this.CodeFixIndex, project, numberOfIterations, cancellationToken).ConfigureAwait(false);

            var newCompilerDiagnostics = GetNewDiagnostics(compilerDiagnostics, await GetCompilerDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false));

            // Check if applying the code fix introduced any new compiler diagnostics
            if (!this.AllowNewCompilerDiagnostics && newCompilerDiagnostics.Any())
            {
                // Format and get the compiler diagnostics again so that the locations make sense in the output
                project = await ReformatProjectDocumentsAsync(project, cancellationToken).ConfigureAwait(false);
                newCompilerDiagnostics = GetNewDiagnostics(compilerDiagnostics, await GetCompilerDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false));

                var message = new StringBuilder();
                message.Append("Fix introduced new compiler diagnostics:\r\n");
                newCompilerDiagnostics.Aggregate(message, (sb, d) => sb.Append(d.ToString()).Append("\r\n"));
                foreach (var document in project.Documents)
                {
                    message.Append("\r\n").Append(document.Name).Append(":\r\n");
                    message.Append((await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)).ToFullString());
                    message.Append("\r\n");
                }

                Assert.True(false, message.ToString());
            }

            // After applying all of the code fixes, compare the resulting string to the inputted one
            var updatedDocuments = project.Documents.ToArray();

            Assert.Equal($"{newSources.Length} documents", $"{updatedDocuments.Length} documents");

            for (int i = 0; i < updatedDocuments.Length; i++)
            {
                var actual = await GetSourceTextFromDocumentAsync(updatedDocuments[i], cancellationToken).ConfigureAwait(false);
                Assert.Equal(newSources[i].content.ToString(), actual.ToString());
                Assert.Equal(newSources[i].content.Encoding, actual.Encoding);
                Assert.Equal(newSources[i].content.ChecksumAlgorithm, actual.ChecksumAlgorithm);
                Assert.Equal(newSources[i].filename, updatedDocuments[i].Name);
            }

            var updatedAdditionalDocuments = project.AdditionalDocuments.ToArray();

            Assert.Equal($"{fixedAdditionalFiles.Length} additional files", $"{updatedAdditionalDocuments.Length} additional files");

            for (int i = 0; i < updatedAdditionalDocuments.Length; i++)
            {
                var actual = await updatedAdditionalDocuments[i].GetTextAsync(cancellationToken).ConfigureAwait(false);
                Assert.Equal(fixedAdditionalFiles[i].content.ToString(), actual.ToString());
                Assert.Equal(fixedAdditionalFiles[i].content.Encoding, actual.Encoding);
                Assert.Equal(fixedAdditionalFiles[i].content.ChecksumAlgorithm, actual.ChecksumAlgorithm);
                Assert.Equal(fixedAdditionalFiles[i].filename, updatedAdditionalDocuments[i].Name);
            }
        }

        private static bool HasAnyChange((string filename, SourceText content)[] oldSources, (string filename, SourceText content)[] newSources)
        {
            return !oldSources.SequenceEqual(newSources, SourceFileEqualityComparer.Instance);
        }

        private static async Task<Project> FixEachAnalyzerDiagnosticAsync(ImmutableArray<DiagnosticAnalyzer> analyzers, ImmutableArray<CodeFixProvider> codeFixProviders, int? codeFixIndex, Project project, int numberOfIterations, CancellationToken cancellationToken)
        {
            var codeFixProvider = codeFixProviders.Single();

            int expectedNumberOfIterations = numberOfIterations;
            if (numberOfIterations < 0)
            {
                numberOfIterations = -numberOfIterations;
            }

            var previousDiagnostics = ImmutableArray.Create<Diagnostic>();

            bool done;
            do
            {
                var analyzerDiagnostics = await GetSortedDiagnosticsFromDocumentsAsync(analyzers, project.Documents.ToArray(), cancellationToken).ConfigureAwait(false);
                if (analyzerDiagnostics.Length == 0)
                {
                    break;
                }

                if (!AreDiagnosticsDifferent(analyzerDiagnostics, previousDiagnostics))
                {
                    break;
                }

                if (--numberOfIterations < -1)
                {
                    Assert.True(false, "The upper limit for the number of code fix iterations was exceeded");
                }

                previousDiagnostics = analyzerDiagnostics;

                done = true;
                bool anyActions = false;
                foreach (var diagnostic in analyzerDiagnostics)
                {
                    if (!codeFixProvider.FixableDiagnosticIds.Contains(diagnostic.Id))
                    {
                        // do not pass unsupported diagnostics to a code fix provider
                        continue;
                    }

                    var actions = new List<CodeAction>();
                    var context = new CodeFixContext(project.GetDocument(diagnostic.Location.SourceTree), diagnostic, (a, d) => actions.Add(a), cancellationToken);
                    await codeFixProvider.RegisterCodeFixesAsync(context).ConfigureAwait(false);

                    if (actions.Count > 0)
                    {
                        anyActions = true;

                        var fixedProject = await ApplyFixAsync(project, actions.ElementAt(codeFixIndex.GetValueOrDefault(0)), cancellationToken).ConfigureAwait(false);
                        if (fixedProject != project)
                        {
                            done = false;

                            project = await RecreateProjectDocumentsAsync(fixedProject, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                    }
                }

                if (!anyActions)
                {
                    Assert.True(done);

                    // Avoid counting iterations that do not provide any code actions
                    numberOfIterations++;
                }
            }
            while (!done);

            if (expectedNumberOfIterations >= 0)
            {
                Assert.Equal($"{expectedNumberOfIterations} iterations", $"{expectedNumberOfIterations - numberOfIterations} iterations");
            }

            return project;
        }

        private static Task<Project> FixAllAnalyzerDiagnosticsInDocumentAsync(ImmutableArray<DiagnosticAnalyzer> analyzers, ImmutableArray<CodeFixProvider> codeFixProviders, int? codeFixIndex, Project project, int numberOfIterations, CancellationToken cancellationToken)
        {
            return FixAllAnalyerDiagnosticsInScopeAsync(FixAllScope.Document, analyzers, codeFixProviders, codeFixIndex, project, numberOfIterations, cancellationToken);
        }

        private static Task<Project> FixAllAnalyzerDiagnosticsInProjectAsync(ImmutableArray<DiagnosticAnalyzer> analyzers, ImmutableArray<CodeFixProvider> codeFixProviders, int? codeFixIndex, Project project, int numberOfIterations, CancellationToken cancellationToken)
        {
            return FixAllAnalyerDiagnosticsInScopeAsync(FixAllScope.Project, analyzers, codeFixProviders, codeFixIndex, project, numberOfIterations, cancellationToken);
        }

        private static Task<Project> FixAllAnalyzerDiagnosticsInSolutionAsync(ImmutableArray<DiagnosticAnalyzer> analyzers, ImmutableArray<CodeFixProvider> codeFixProviders, int? codeFixIndex, Project project, int numberOfIterations, CancellationToken cancellationToken)
        {
            return FixAllAnalyerDiagnosticsInScopeAsync(FixAllScope.Solution, analyzers, codeFixProviders, codeFixIndex, project, numberOfIterations, cancellationToken);
        }

        private static async Task<Project> FixAllAnalyerDiagnosticsInScopeAsync(FixAllScope scope, ImmutableArray<DiagnosticAnalyzer> analyzers, ImmutableArray<CodeFixProvider> codeFixProviders, int? codeFixIndex, Project project, int numberOfIterations, CancellationToken cancellationToken)
        {
            var codeFixProvider = codeFixProviders.Single();

            int expectedNumberOfIterations = numberOfIterations;
            if (numberOfIterations < 0)
            {
                numberOfIterations = -numberOfIterations;
            }

            var previousDiagnostics = ImmutableArray.Create<Diagnostic>();

            var fixAllProvider = codeFixProvider.GetFixAllProvider();

            if (fixAllProvider == null)
            {
                return null;
            }

            bool done;
            do
            {
                var analyzerDiagnostics = await GetSortedDiagnosticsFromDocumentsAsync(analyzers, project.Documents.ToArray(), cancellationToken).ConfigureAwait(false);
                if (analyzerDiagnostics.Length == 0)
                {
                    break;
                }

                if (!AreDiagnosticsDifferent(analyzerDiagnostics, previousDiagnostics))
                {
                    break;
                }

                if (--numberOfIterations < -1)
                {
                    Assert.True(false, "The upper limit for the number of fix all iterations was exceeded");
                }

                Diagnostic firstDiagnostic = null;
                string equivalenceKey = null;
                foreach (var diagnostic in analyzerDiagnostics)
                {
                    if (!codeFixProvider.FixableDiagnosticIds.Contains(diagnostic.Id))
                    {
                        // do not pass unsupported diagnostics to a code fix provider
                        continue;
                    }

                    var actions = new List<CodeAction>();
                    var context = new CodeFixContext(project.GetDocument(diagnostic.Location.SourceTree), diagnostic, (a, d) => actions.Add(a), cancellationToken);
                    await codeFixProvider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                    if (actions.Count > (codeFixIndex ?? 0))
                    {
                        firstDiagnostic = diagnostic;
                        equivalenceKey = actions[codeFixIndex ?? 0].EquivalenceKey;
                        break;
                    }
                }

                if (firstDiagnostic == null)
                {
                    numberOfIterations++;
                    break;
                }

                previousDiagnostics = analyzerDiagnostics;

                done = true;

                FixAllContext.DiagnosticProvider fixAllDiagnosticProvider = TestDiagnosticProvider.Create(analyzerDiagnostics);

                IEnumerable<string> analyzerDiagnosticIds = analyzers.SelectMany(x => x.SupportedDiagnostics).Select(x => x.Id);
                IEnumerable<string> compilerDiagnosticIds = codeFixProvider.FixableDiagnosticIds.Where(x => x.StartsWith("CS", StringComparison.Ordinal));
                IEnumerable<string> disabledDiagnosticIds = project.CompilationOptions.SpecificDiagnosticOptions.Where(x => x.Value == ReportDiagnostic.Suppress).Select(x => x.Key);
                IEnumerable<string> relevantIds = analyzerDiagnosticIds.Concat(compilerDiagnosticIds).Except(disabledDiagnosticIds).Distinct();
                FixAllContext fixAllContext = new FixAllContext(project.GetDocument(firstDiagnostic.Location.SourceTree), codeFixProvider, scope, equivalenceKey, relevantIds, fixAllDiagnosticProvider, cancellationToken);

                CodeAction action = await fixAllProvider.GetFixAsync(fixAllContext).ConfigureAwait(false);
                if (action == null)
                {
                    return project;
                }

                var fixedProject = await ApplyFixAsync(project, action, cancellationToken).ConfigureAwait(false);
                if (fixedProject != project)
                {
                    done = false;

                    project = await RecreateProjectDocumentsAsync(fixedProject, cancellationToken).ConfigureAwait(false);
                }
            }
            while (!done);

            if (expectedNumberOfIterations >= 0)
            {
                Assert.Equal($"{expectedNumberOfIterations} iterations", $"{expectedNumberOfIterations - numberOfIterations} iterations");
            }

            return project;
        }

        /// <summary>
        /// Get the existing compiler diagnostics on the input document.
        /// </summary>
        /// <param name="project">The <see cref="Project"/> to run the compiler diagnostic analyzers on.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>The compiler diagnostics that were found in the code.</returns>
        private static async Task<ImmutableArray<Diagnostic>> GetCompilerDiagnosticsAsync(Project project, CancellationToken cancellationToken)
        {
            var allDiagnostics = ImmutableArray.Create<Diagnostic>();

            foreach (var document in project.Documents)
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                allDiagnostics = allDiagnostics.AddRange(semanticModel.GetDiagnostics(cancellationToken: cancellationToken));
            }

            return allDiagnostics;
        }

        /// <summary>
        /// Given a document, turn it into a string based on the syntax root.
        /// </summary>
        /// <param name="document">The <see cref="Document"/> to be converted to a string.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A string containing the syntax of the <see cref="Document"/> after formatting.</returns>
        private static async Task<SourceText> GetSourceTextFromDocumentAsync(Document document, CancellationToken cancellationToken)
        {
            var simplifiedDoc = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var formatted = await Formatter.FormatAsync(simplifiedDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Implements a workaround for issue #936, force re-parsing to get the same sort of syntax tree as the original document.
        /// </summary>
        /// <param name="project">The project to update.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <returns>The updated <see cref="Project"/>.</returns>
        private static async Task<Project> RecreateProjectDocumentsAsync(Project project, CancellationToken cancellationToken)
        {
            foreach (var documentId in project.DocumentIds)
            {
                var document = project.GetDocument(documentId);
                document = await RecreateDocumentAsync(document, cancellationToken).ConfigureAwait(false);
                project = document.Project;
            }

            return project;
        }

        private static async Task<Document> RecreateDocumentAsync(Document document, CancellationToken cancellationToken)
        {
            var newText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            newText = newText.WithChanges(new TextChange(new TextSpan(0, 0), " "));
            newText = newText.WithChanges(new TextChange(new TextSpan(0, 1), string.Empty));
            return document.WithText(newText);
        }

        /// <summary>
        /// Formats the whitespace in all documents of the specified <see cref="Project"/>.
        /// </summary>
        /// <param name="project">The project to update.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <returns>The updated <see cref="Project"/>.</returns>
        private static async Task<Project> ReformatProjectDocumentsAsync(Project project, CancellationToken cancellationToken)
        {
            foreach (var documentId in project.DocumentIds)
            {
                var document = project.GetDocument(documentId);
                document = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
                project = document.Project;
            }

            return project;
        }

        /// <summary>
        /// Compare two collections of <see cref="Diagnostic"/>s, and return a list of any new diagnostics that appear
        /// only in the second collection.
        /// <note type="note">
        /// <para>Considers <see cref="Diagnostic"/> to be the same if they have the same <see cref="Diagnostic.Id"/>s.
        /// In the case of multiple diagnostics with the same <see cref="Diagnostic.Id"/> in a row, this method may not
        /// necessarily return the new one.</para>
        /// </note>
        /// </summary>
        /// <param name="diagnostics">The <see cref="Diagnostic"/>s that existed in the code before the code fix was
        /// applied.</param>
        /// <param name="newDiagnostics">The <see cref="Diagnostic"/>s that exist in the code after the code fix was
        /// applied.</param>
        /// <returns>A list of <see cref="Diagnostic"/>s that only surfaced in the code after the code fix was
        /// applied.</returns>
        private static IEnumerable<Diagnostic> GetNewDiagnostics(IEnumerable<Diagnostic> diagnostics, IEnumerable<Diagnostic> newDiagnostics)
        {
            var oldArray = diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
            var newArray = newDiagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();

            int oldIndex = 0;
            int newIndex = 0;

            while (newIndex < newArray.Length)
            {
                if (oldIndex < oldArray.Length && oldArray[oldIndex].Id == newArray[newIndex].Id)
                {
                    ++oldIndex;
                    ++newIndex;
                }
                else
                {
                    yield return newArray[newIndex++];
                }
            }
        }

        /// <summary>
        /// Apply the inputted <see cref="CodeAction"/> to the inputted document.
        /// Meant to be used to apply code fixes.
        /// </summary>
        /// <param name="project">The <see cref="Project"/> to apply the fix on.</param>
        /// <param name="codeAction">A <see cref="CodeAction"/> that will be applied to the
        /// <paramref name="project"/>.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A <see cref="Project"/> with the changes from the <see cref="CodeAction"/>.</returns>
        private static async Task<Project> ApplyFixAsync(Project project, CodeAction codeAction, CancellationToken cancellationToken)
        {
            var operations = await codeAction.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            var solution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            return solution.GetProject(project.Id);
        }

        private static bool AreDiagnosticsDifferent(ImmutableArray<Diagnostic> analyzerDiagnostics, ImmutableArray<Diagnostic> previousDiagnostics)
        {
            if (analyzerDiagnostics.Length != previousDiagnostics.Length)
            {
                return true;
            }

            for (var i = 0; i < analyzerDiagnostics.Length; i++)
            {
                if ((analyzerDiagnostics[i].Id != previousDiagnostics[i].Id)
                    || (analyzerDiagnostics[i].Location.SourceSpan != previousDiagnostics[i].Location.SourceSpan))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class SourceFileEqualityComparer : IEqualityComparer<(string filename, SourceText content)>
        {
            private SourceFileEqualityComparer()
            {
            }

            public static SourceFileEqualityComparer Instance { get; } = new SourceFileEqualityComparer();

            public bool Equals((string filename, SourceText content) x, (string filename, SourceText content) y)
            {
                if (x.filename != y.filename)
                {
                    return false;
                }

                if (x.content is null || y.content is null)
                {
                    return ReferenceEquals(x, y);
                }

                return x.content.Encoding == y.content.Encoding
                    && x.content.ChecksumAlgorithm == y.content.ChecksumAlgorithm
                    && x.content.ContentEquals(y.content);
            }

            public int GetHashCode((string filename, SourceText content) obj)
            {
                return obj.filename.GetHashCode()
                    ^ (obj.content?.ToString().GetHashCode() ?? 0);
            }
        }
    }
}
