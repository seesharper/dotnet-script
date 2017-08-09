using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Dotnet.Script.Core.Internal;
using Dotnet.Script.Core.Metadata;
using Dotnet.Script.Core.NuGet;
using Dotnet.Script.Core.ProjectSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Microsoft.DotNet.PlatformAbstractions;
using Microsoft.DotNet.ProjectModel;
using Microsoft.Extensions.DependencyModel;

namespace Dotnet.Script.Core
{
    public class ScriptCompiler
    {
        private readonly ScriptLogger _logger;
        private readonly ScriptProjectProvider _scriptProjectProvider;

        protected virtual IEnumerable<Assembly> ReferencedAssemblies => new[]
        {
            typeof(object).GetTypeInfo().Assembly,
            typeof(Enumerable).GetTypeInfo().Assembly
        };

        protected virtual IEnumerable<string> ImportedNamespaces => new[]
        {
            "System",
            "System.IO",
            "System.Collections.Generic",
            "System.Console",
            "System.Diagnostics",
            "System.Dynamic",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Text",
            "System.Threading.Tasks"
        };

        // see: https://github.com/dotnet/roslyn/issues/5501
        protected virtual IEnumerable<string> SuppressedDiagnosticIds => new[] { "CS1701", "CS1702", "CS1705" };

        public ScriptCompiler(ScriptLogger logger, ScriptProjectProvider scriptProjectProvider)
        {
            _logger = logger;
            _scriptProjectProvider = scriptProjectProvider;
        }

        public virtual ScriptOptions CreateScriptOptions(ScriptContext context)
        {
            var opts = ScriptOptions.Default.AddImports(ImportedNamespaces)
                .AddReferences(ReferencedAssemblies)
                .WithSourceResolver(new SourceFileResolver(ImmutableArray<string>.Empty, context.WorkingDirectory))
                .WithMetadataResolver(new NuGetMetadataReferenceResolver(ScriptMetadataResolver.Default))
                .WithEmitDebugInformation(context.DebugMode)
                .WithFileEncoding(context.Code.Encoding);

            if (!string.IsNullOrWhiteSpace(context.FilePath))
            {
                opts = opts.WithFilePath(context.FilePath);
            }
            
            return opts;
        }

        public virtual ScriptCompilationContext<TReturn> CreateCompilationContext<TReturn, THost>(ScriptContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var runtimeIdentitfer = RuntimeHelper.GetRuntimeIdentitifer();
            _logger.Verbose($"Current runtime is '{runtimeIdentitfer}'.");

            var opts = CreateScriptOptions(context);

            var runtimeId = RuntimeEnvironment.GetRuntimeIdentifier();
            var inheritedAssemblyNames = DependencyContext.Default.GetRuntimeAssemblyNames(runtimeId).Where(x =>
                x.FullName.StartsWith("system.", StringComparison.OrdinalIgnoreCase) ||
                x.FullName.StartsWith("microsoft.codeanalysis", StringComparison.OrdinalIgnoreCase) ||
                x.FullName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase));

            foreach (var inheritedAssemblyName in inheritedAssemblyNames)
            {
                _logger.Verbose("Adding reference to an inherited dependency => " + inheritedAssemblyName.FullName);
                var assembly = Assembly.Load(inheritedAssemblyName);
                opts = opts.AddReferences(assembly);
            }

            IEnumerable<RuntimeDependency> runtimeDependencies;
            var pathToProjectJson = Path.Combine(context.WorkingDirectory, Project.FileName);
            
            if (!File.Exists(pathToProjectJson))
            {
                _logger.Verbose("Unable to find project context for CSX files. Will default to non-context usage.");                
                var pathToCsProj = _scriptProjectProvider.CreateProject(context.WorkingDirectory);
                var dependencyResolver = new DependencyResolver(new CommandRunner(_logger), _logger);
                runtimeDependencies = dependencyResolver.GetRuntimeDependencies(pathToCsProj);
            }
            else
            {
                _logger.Verbose($"Found runtime context for '{pathToProjectJson}'.");
                var dependencyResolver = new LegacyDependencyResolver(_logger);
                runtimeDependencies = dependencyResolver.GetRuntimeDependencies(pathToProjectJson);
            }
                                   
            foreach (var runtimeDep in runtimeDependencies)
            {
                _logger.Verbose("Adding reference to a runtime dependency => " + runtimeDep);
                opts = opts.AddReferences(MetadataReference.CreateFromFile(runtimeDep.Path));
            }

            var loader = new InteractiveAssemblyLoader();
            var script = CSharpScript.Create<TReturn>(context.Code.ToString(), opts, typeof(THost), loader);
            var compilation = script.GetCompilation();

            ProcessCompilationDiagnostics(compilation);

            return new ScriptCompilationContext<TReturn>(script, context.Code, loader);
        }

        private void ProcessCompilationDiagnostics(Compilation compilation)
        {
            var diagnostics = compilation.GetDiagnostics().Where(d => !SuppressedDiagnosticIds.Contains(d.Id));
            var orderedDiagnostics = diagnostics.OrderBy((d1, d2) =>
            {
                var severityDiff = (int) d2.Severity - (int) d1.Severity;
                return severityDiff != 0 ? severityDiff : d1.Location.SourceSpan.Start - d2.Location.SourceSpan.Start;
            });

            if (orderedDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                foreach (var diagnostic in orderedDiagnostics)
                {
                    _logger.Log(diagnostic.ToString());
                }

                throw new CompilationErrorException("Script compilation failed due to one or more errors.",
                    orderedDiagnostics.ToImmutableArray());
            }
        }
    }
}