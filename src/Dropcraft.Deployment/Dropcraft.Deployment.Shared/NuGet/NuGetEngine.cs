﻿
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dropcraft.Common;
using Dropcraft.Common.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.PackageManagement;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Dropcraft.Deployment.NuGet
{
    internal class NuGetEngine
    {
        private readonly IDeploymentContext _deploymentContext;
        private readonly bool _updatePackages;

        private readonly DropcraftProject _project;
        private readonly SourceRepositoryProvider _repositoryProvider;
        private readonly NuGetPackageManager _nuGetPackageManager;
        private readonly SourceRepository _localRepository;
        private readonly NuGetLogger _nuGetLogger;
        private readonly NuGetFramework _currentFramework;
        private static readonly ILog Logger = LogProvider.For<NuGetEngine>();

        public NuGetEngine(DeploymentConfiguration configuration)
        {
            _deploymentContext = configuration.DeploymentContext;
            _updatePackages = configuration.UpdatePackages;

            var settings = Settings.LoadDefaultSettings(_deploymentContext.PackagesFolderPath);

            _nuGetLogger = new NuGetLogger();
            _currentFramework = GetCurrentFramework();
            _repositoryProvider = new SourceRepositoryProvider(settings);
            _localRepository = _repositoryProvider.CreateRepository(_deploymentContext.PackagesFolderPath);
            _project = new DropcraftProject(_deploymentContext.PackagesFolderPath)
            {
                CurrentFramework = _currentFramework,
                DefaultConflictResolution = configuration.DefaultConflictResolution
            };

            _nuGetPackageManager = new NuGetPackageManager(_repositoryProvider, settings, _deploymentContext.PackagesFolderPath)
            {
                PackagesFolderNuGetProject = _project
            };

            foreach (var packageSource in configuration.PackageSources)
            {
                _repositoryProvider.AddPackageRepository(packageSource);
            }

        }

        public async Task<ActionablePackage> ResolveInstallablePackage(PackageId packageInfo)
        {
            Logger.Trace($"Resolving package {packageInfo.Id} {packageInfo.VersionRange}");

            NuGetVersion resolvedVersion = null;
            if (!_updatePackages && !string.IsNullOrWhiteSpace(packageInfo.VersionRange))
            {
                resolvedVersion = await GetLatestMatchingVersion(packageInfo, _localRepository, _nuGetLogger);
            }

            if (resolvedVersion != null)
            {
                Logger.Trace($"Package {packageInfo.Id} resolved from {_deploymentContext.PackagesFolderPath} using version {resolvedVersion.Version}");
            }
            else
            if (_repositoryProvider.Repositories.Count > 0)
            {
                resolvedVersion = await GetLatestMatchingVersion(packageInfo, _repositoryProvider.Repositories, _nuGetLogger);
            }

            if (resolvedVersion == null)
            {
                Logger.Trace($"Package {packageInfo.Id} {packageInfo.VersionRange} cannot be resolved");
            }

            return new ActionablePackage(packageInfo, resolvedVersion);
        }

        public async Task<List<ActionablePackage>> InstallPackage(ActionablePackage package)
        {
            Logger.Trace($"Installing package {package.PackageId.Id} {package.Version} with dependencies");

            _project.CleanRecentPackages();
            var resolutionContext = new ResolutionContext(DependencyBehavior.Lowest, package.PackageId.AllowPrereleaseVersions,
                                                                                        false, VersionConstraints.None);

            var projectContext = new ProjectContext();
            await _nuGetPackageManager.InstallPackageAsync(_nuGetPackageManager.PackagesFolderNuGetProject,
                    new PackageIdentity(package.PackageId.Id, package.Version), resolutionContext, projectContext,
                    _repositoryProvider.Repositories, Array.Empty<SourceRepository>(), CancellationToken.None);

            Logger.Trace($"Installed package {package.PackageId.Id} {package.Version}");
            return _project.ProcessRecentPackages();
        }

        private async Task<NuGetVersion> GetLatestMatchingVersion(PackageId packageInfo, IEnumerable<SourceRepository> sourceRepositories, ILogger logger)
        {
            NuGetVersion[] versionMatches = await Task.WhenAll(sourceRepositories.Select(x => GetLatestMatchingVersion(packageInfo, x, logger)));
            return versionMatches
                .Where(x => x != null)
                .DefaultIfEmpty()
                .Max();
        }

        private async Task<NuGetVersion> GetLatestMatchingVersion(PackageId packageInfo, SourceRepository sourceRepository, ILogger logger)
        {
            try
            {
                var versionRange = string.IsNullOrWhiteSpace(packageInfo.VersionRange) ? null : VersionRange.Parse(packageInfo.VersionRange);
                DependencyInfoResource dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>();
                IEnumerable<SourcePackageDependencyInfo> dependencyInfo = await dependencyInfoResource.ResolvePackages(
                    packageInfo.Id, _currentFramework, logger, CancellationToken.None);
                return dependencyInfo
                    .Select(x => x.Version)
                    .Where(x => x != null && (versionRange == null || versionRange.Satisfies(x)) && (packageInfo.AllowPrereleaseVersions || !x.IsPrerelease))
                    .DefaultIfEmpty()
                    .Max();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not get latest version for package {packageInfo.Id}{packageInfo.VersionRange} from source {sourceRepository}: {ex.Message}");
                return null;
            }
        }
        private NuGetFramework GetCurrentFramework()
        {
            string frameworkName = Assembly.GetExecutingAssembly().GetCustomAttributes(true)
                .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>()
                .Select(x => x.FrameworkName)
                .FirstOrDefault();
            return frameworkName == null
                ? NuGetFramework.AnyFramework
                : NuGetFramework.ParseFrameworkName(frameworkName, new DefaultFrameworkNameProvider());
        }

    }
}