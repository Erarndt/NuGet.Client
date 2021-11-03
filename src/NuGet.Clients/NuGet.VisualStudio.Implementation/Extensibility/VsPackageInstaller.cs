// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.PackageManagement;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.PackageExtraction;
using NuGet.Packaging.Signing;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using NuGet.VisualStudio.Implementation.Resources;
using NuGet.VisualStudio.Telemetry;
using Task = System.Threading.Tasks.Task;

namespace NuGet.VisualStudio
{
    [Export(typeof(IVsPackageInstaller))]
    [Export(typeof(IVsPackageInstaller2))]
    public class VsPackageInstaller : IVsPackageInstaller2
    {
        private readonly ISourceRepositoryProvider _sourceRepositoryProvider;
        private readonly ISettings _settings;
        private readonly IVsSolutionManager _solutionManager;
        private readonly IDeleteOnRestartManager _deleteOnRestartManager;
        private readonly INuGetTelemetryProvider _telemetryProvider;

        private JoinableTaskFactory PumpingJTF { get; set; }

        [ImportingConstructor]
        public VsPackageInstaller(
            ISourceRepositoryProvider sourceRepositoryProvider,
            ISettings settings,
            IVsSolutionManager solutionManager,
            IDeleteOnRestartManager deleteOnRestartManager,
            INuGetTelemetryProvider telemetryProvider)
        {
            _sourceRepositoryProvider = sourceRepositoryProvider;
            _settings = settings;
            _solutionManager = solutionManager;
            _deleteOnRestartManager = deleteOnRestartManager;
            _telemetryProvider = telemetryProvider;

            PumpingJTF = new PumpingJTF(NuGetUIThreadHelper.JoinableTaskFactory);
        }

        public void InstallLatestPackage(
            string source,
            Project project,
            string packageId,
            bool includePrerelease,
            bool ignoreDependencies)
        {
            try
            {
                PumpingJTF.Run(() => InstallPackageAsync(
                    source,
                    project,
                    packageId,
                    version: null,
                    includePrerelease: includePrerelease,
                    ignoreDependencies: ignoreDependencies));
            }
            catch (Exception exception)
            {
                _telemetryProvider.PostFault(exception, typeof(VsPackageInstaller).FullName);
                throw;
            }
        }

        public void InstallPackage(string source, Project project, string packageId, Version version, bool ignoreDependencies)
        {
            try
            {
                NuGetVersion semVer = null;

                if (version != null)
                {
                    semVer = new NuGetVersion(version);
                }

                PumpingJTF.Run(() => InstallPackageAsync(
                    source,
                    project,
                    packageId,
                    version: semVer,
                    includePrerelease: false,
                    ignoreDependencies: ignoreDependencies));
            }
            catch (Exception exception)
            {
                _telemetryProvider.PostFault(exception, typeof(VsPackageInstaller).FullName);
                throw;
            }
        }

        public void InstallPackage(string source, Project project, string packageId, string version, bool ignoreDependencies)
        {
            try
            {
                NuGetVersion semVer = null;

                if (!string.IsNullOrEmpty(version))
                {
                    _ = NuGetVersion.TryParse(version, out semVer);
                }

                PumpingJTF.Run(() => InstallPackageAsync(
                    source,
                    project,
                    packageId,
                    version: semVer,
                    includePrerelease: false,
                    ignoreDependencies: ignoreDependencies));
            }
            catch (Exception exception)
            {
                _telemetryProvider.PostFault(exception, typeof(VsPackageInstaller).FullName);
                throw;
            }
        }

        // Calls from the installer sync APIs end up here.
        private Task InstallPackageAsync(string source, Project project, string packageId, NuGetVersion version, bool includePrerelease, bool ignoreDependencies)
        {
            (IEnumerable<string> sources, List<PackageIdentity> toInstall, VSAPIProjectContext projectContext) = PrepForInstallation(_settings, source, packageId, version, isAllRespected: true);
            Task<NuGetProject> getNuGetProjectAsync(IVsSolutionManager vsSolutionManager) => GetNuGetProjectAsync(vsSolutionManager, project, projectContext);
            return PackageServiceUtilities.InstallInternalAsync(getNuGetProjectAsync, toInstall, GetSources(sources), _solutionManager, _settings, _deleteOnRestartManager, projectContext, includePrerelease, ignoreDependencies, CancellationToken.None);
        }

        internal static (IEnumerable<string>, List<PackageIdentity> toInstall, VSAPIProjectContext projectContext) PrepForInstallation(ISettings settings, string source, string packageId, NuGetVersion version, bool isAllRespected)
        {
            string[] sources = null;
            if (!string.IsNullOrEmpty(source) &&
                !(isAllRespected && StringComparer.OrdinalIgnoreCase.Equals("All", source))) // "All" was supported in V2
            {
                sources = new[] { source };
            }

            var toInstall = new List<PackageIdentity>
            {
                new PackageIdentity(packageId, version)
            };

            var projectContext = new VSAPIProjectContext
            {
                PackageExtractionContext = new PackageExtractionContext(
                    PackageSaveMode.Defaultv2,
                    PackageExtractionBehavior.XmlDocFileSaveMode,
                    ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance),
                    NullLogger.Instance)
            };
            return (sources, toInstall, projectContext);
        }

        private static async Task<NuGetProject> GetNuGetProjectAsync(IVsSolutionManager vsSolutionManager, Project project, INuGetProjectContext projectContext)
        {
            return await vsSolutionManager.GetOrCreateProjectAsync(project, projectContext);
        }

        public void InstallPackage(IPackageRepository repository, Project project, string packageId, string version, bool ignoreDependencies, bool skipAssemblyReferences)
        {
            // It would be really difficult for anyone to use this method
            throw new NotSupportedException();
        }

        public void InstallPackagesFromRegistryRepository(string keyName, bool isPreUnzipped, bool skipAssemblyReferences, Project project, IDictionary<string, string> packageVersions)
        {
            InstallPackagesFromRegistryRepository(keyName, isPreUnzipped, skipAssemblyReferences, ignoreDependencies: true, project: project, packageVersions: packageVersions);
        }

        public void InstallPackagesFromRegistryRepository(string keyName, bool isPreUnzipped, bool skipAssemblyReferences, bool ignoreDependencies, Project project, IDictionary<string, string> packageVersions)
        {
            if (string.IsNullOrEmpty(keyName))
            {
                throw new ArgumentException(CommonResources.Argument_Cannot_Be_Null_Or_Empty, nameof(keyName));
            }

            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (packageVersions == null
                || !packageVersions.Any())
            {
                throw new ArgumentException(CommonResources.Argument_Cannot_Be_Null_Or_Empty, nameof(packageVersions));
            }

            try
            {
                PumpingJTF.Run(async () =>
                    {
                        // HACK !!! : This is a hack for PCL projects which send isPreUnzipped = true, but their package source 
                        // (located at C:\Program Files (x86)\Microsoft SDKs\NuGetPackages) follows the V3
                        // folder version format.
                        if (isPreUnzipped)
                        {
                            var isProjectJsonProject = await EnvDTEProjectUtility.HasBuildIntegratedConfig(project);
                            isPreUnzipped = isProjectJsonProject ? false : isPreUnzipped;
                        }

                        // create a repository provider with only the registry repository
                        var repoProvider = new PreinstalledRepositoryProvider(ErrorHandler, _sourceRepositoryProvider);
                        repoProvider.AddFromRegistry(keyName, isPreUnzipped);

                        var toInstall = GetIdentitiesFromDict(packageVersions);

                        // Skip assembly references and disable binding redirections should be done together
                        var disableBindingRedirects = skipAssemblyReferences;

                        var projectContext = new VSAPIProjectContext(skipAssemblyReferences, disableBindingRedirects)
                        {
                            PackageExtractionContext = new PackageExtractionContext(
                                PackageSaveMode.Defaultv2,
                                PackageExtractionBehavior.XmlDocFileSaveMode,
                                ClientPolicyContext.GetClientPolicy(_settings, NullLogger.Instance),
                                NullLogger.Instance)
                        };
                        Task<NuGetProject> getNuGetProjectAsync(IVsSolutionManager vsSolutionManager) => GetNuGetProjectAsync(vsSolutionManager, project, projectContext);

                        await PackageServiceUtilities.InstallInternalAsync(
                            getNuGetProjectAsync,
                            toInstall,
                            repoProvider,
                            _solutionManager,
                            _settings,
                            _deleteOnRestartManager,
                            projectContext,
                            includePrerelease: false,
                            ignoreDependencies: ignoreDependencies,
                            token: CancellationToken.None);
                    });
            }
            catch (Exception exception)
            {
                _telemetryProvider.PostFault(exception, typeof(VsPackageInstaller).FullName);
                throw;
            }
        }

        public void InstallPackagesFromVSExtensionRepository(string extensionId, bool isPreUnzipped, bool skipAssemblyReferences, Project project, IDictionary<string, string> packageVersions)
        {
            InstallPackagesFromVSExtensionRepository(
                extensionId,
                isPreUnzipped,
                skipAssemblyReferences,
                ignoreDependencies: true,
                project: project,
                packageVersions: packageVersions);
        }

        public void InstallPackagesFromVSExtensionRepository(string extensionId, bool isPreUnzipped, bool skipAssemblyReferences, bool ignoreDependencies, Project project, IDictionary<string, string> packageVersions)
        {
            if (string.IsNullOrEmpty(extensionId))
            {
                throw new ArgumentException(CommonResources.Argument_Cannot_Be_Null_Or_Empty, nameof(extensionId));
            }

            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (!packageVersions.Any())
            {
                throw new ArgumentException(CommonResources.Argument_Cannot_Be_Null_Or_Empty, nameof(packageVersions));
            }

            try
            {
                PumpingJTF.Run(() =>
                    {
                        var repoProvider = new PreinstalledRepositoryProvider(ErrorHandler, _sourceRepositoryProvider);
                        repoProvider.AddFromExtension(_sourceRepositoryProvider, extensionId);

                        var toInstall = GetIdentitiesFromDict(packageVersions);

                        // Skip assembly references and disable binding redirections should be done together
                        var disableBindingRedirects = skipAssemblyReferences;

                        var projectContext = new VSAPIProjectContext(skipAssemblyReferences, disableBindingRedirects)
                        {
                            PackageExtractionContext = new PackageExtractionContext(
                                PackageSaveMode.Defaultv2,
                                PackageExtractionBehavior.XmlDocFileSaveMode,
                                ClientPolicyContext.GetClientPolicy(_settings, NullLogger.Instance),
                                NullLogger.Instance)
                        };

                        Task<NuGetProject> getNuGetProjectAsync(IVsSolutionManager vsSolutionManager) => GetNuGetProjectAsync(vsSolutionManager, project, projectContext);
                        return PackageServiceUtilities.InstallInternalAsync(
                            getNuGetProjectAsync,
                            toInstall,
                            repoProvider,
                            _solutionManager,
                            _settings,
                            _deleteOnRestartManager,
                            projectContext,
                            includePrerelease: false,
                            ignoreDependencies: ignoreDependencies,
                            token: CancellationToken.None);
                });
            }
            catch (Exception exception)
            {
                _telemetryProvider.PostFault(exception, typeof(VsPackageInstaller).FullName);
                throw;
            }
        }

        private static List<PackageIdentity> GetIdentitiesFromDict(IDictionary<string, string> packageVersions)
        {
            var toInstall = new List<PackageIdentity>();

            // create identities
            foreach (var pair in packageVersions)
            {
                // Version can be null.
                _ = NuGetVersion.TryParse(pair.Value, out NuGetVersion version);
                toInstall.Add(new PackageIdentity(pair.Key, version));
            }

            return toInstall;
        }

        private static Action<string> ErrorHandler => msg =>
        {
            // We don't log anything
        };

        /// <summary>
        /// Creates a repo provider for the given sources. If null is passed all sources will be returned.
        /// </summary>
        private ISourceRepositoryProvider GetSources(IEnumerable<string> sources)
        {
            ISourceRepositoryProvider provider = null;

            // add everything enabled if null
            if (sources == null)
            {
                // Use the default set of sources
                provider = _sourceRepositoryProvider;
            }
            else
            {
                // Create a custom source provider for the VS API install
                var customProvider = new PreinstalledRepositoryProvider(ErrorHandler, _sourceRepositoryProvider);

                // Create sources using the given set of sources
                foreach (var source in sources)
                {
                    customProvider.AddFromSource(GetSource(source));
                }

                provider = customProvider;
            }

            return provider;
        }

        /// <summary>
        /// Convert a source string to a SourceRepository. If one already exists that will be used.
        /// </summary>
        private SourceRepository GetSource(string source)
        {
            var repo = _sourceRepositoryProvider.GetRepositories()
                .Where(e => StringComparer.OrdinalIgnoreCase.Equals(e.PackageSource.Source, source)).FirstOrDefault();

            if (repo == null)
            {
                Uri result;
                if (!Uri.TryCreate(source, UriKind.Absolute, out result))
                {
                    throw new ArgumentException(
                        string.Format(VsResources.InvalidSource, source),
                        nameof(source));
                }

                var newSource = new PackageSource(source);

                repo = _sourceRepositoryProvider.CreateRepository(newSource);
            }

            return repo;
        }

        /// <summary>
        /// Create a new NuGetPackageManager with the IVsPackageInstaller settings.
        /// </summary>
        internal NuGetPackageManager CreatePackageManager(ISourceRepositoryProvider repoProvider)
        {
            return PackageServiceUtilities.CreatePackageManager(
                repoProvider,
                _settings,
                _solutionManager,
                _deleteOnRestartManager);
        }
    }
}
