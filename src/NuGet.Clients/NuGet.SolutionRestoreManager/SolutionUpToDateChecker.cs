// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;

using NuGet.Commands;
using NuGet.Common;
using NuGet.ProjectModel;

namespace NuGet.SolutionRestoreManager
{
    [Export(typeof(ISolutionRestoreChecker))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class SolutionUpToDateChecker : ISolutionRestoreChecker
    {
        private IList<string> _failedProjects = new List<string>();
        private DependencyGraphSpec _cachedDependencyGraphSpec;
        private Dictionary<string, OutputWriteTime> _outputWriteTimes = new Dictionary<string, OutputWriteTime>();

        public void ReportStatus(IReadOnlyList<RestoreSummary> restoreSummaries)
        {
            _failedProjects.Clear();

            foreach (var summary in restoreSummaries)
            {
                if (summary.Success)
                {
                    var packageSpec = _cachedDependencyGraphSpec.GetProjectSpec(summary.InputPath);
                    GetOutputFilePaths(packageSpec, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath);

                    _outputWriteTimes[summary.InputPath] = new OutputWriteTime()
                    {
                        _lastAssetsFileWriteTime = GetLastWriteTime(assetsFilePath),
                        _lastTargetsFileWriteTime = GetLastWriteTime(targetsFilePath),
                        _lastPropsFileWriteTime = GetLastWriteTime(propsFilePath),
                        _lastLockFileWriteTime = GetLastWriteTime(lockFilePath)
                    };
                }
                else
                {
                    _failedProjects.Add(summary.InputPath);
                }

            }
        }

        // The algorithm here is a 2 pass. In reality the 2nd pass can do a lot but for huge benefits :)
        // Pass #1
        // We check all the specs against the cached ones if any. Any project with a change in the spec is considered dirty.
        // If a project had previously been restored and it failed, it is considered dirty.
        // Every project that is considered to have a dirty spec will be important in pass #2.
        // In the first pass, we also validate the outputs for the projects. Note that these are independent and project specific. Outputs not being up to date it irrelevant for transitivity.
        // Pass #2
        // For every project with a dirty spec (the outputs don't matter here), we want to ensure that its parent projects are marked as dirty as well.
        // This is a bit more expensive since PackageSpecs do not retain pointers to the projects that reference them as ProjectReference.
        // Finally we only update the cache specs if Pass #1 determined that there are projects that are not up to date.
        // Result
        // Lastly all the projects marked as having dirty specs & dirty outputs are returned.
        public IEnumerable<string> PerformUpToDateCheck(DependencyGraphSpec dependencyGraphSpec)
        {
            if (_cachedDependencyGraphSpec != null)
            {
                var dirtySpecs = new List<string>();
                var dirtyOutputs = new List<string>();

                // Pass #1. Validate all the data (i/o)
                // 1a. Validate the package specs (references & settings)
                // 1b. Validate the expected outputs (assets file, nuget.g.*, lock file)
                foreach (var project in dependencyGraphSpec.Projects)
                {
                    var projectUniqueName = project.RestoreMetadata.ProjectUniqueName;
                    var cache = _cachedDependencyGraphSpec.GetProjectSpec(projectUniqueName);

                    if (cache == null || !project.Equals(cache))
                    {
                        dirtySpecs.Add(projectUniqueName);
                    }

                    if (project.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference ||
                        project.RestoreMetadata.ProjectStyle == ProjectStyle.ProjectJson)
                    {
                        if (!_failedProjects.Contains(projectUniqueName) && _outputWriteTimes.TryGetValue(projectUniqueName, out OutputWriteTime outputWriteTime))
                        {
                            GetOutputFilePaths(project, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath);
                            if (!AreOutputsUpToDate(assetsFilePath, targetsFilePath, propsFilePath, lockFilePath, outputWriteTime))
                            {
                                dirtyOutputs.Add(projectUniqueName);
                            }
                        }
                        else
                        {
                            dirtyOutputs.Add(projectUniqueName);
                        }
                    }
                }

                // Fast path. Skip Pass #2
                if (dirtySpecs.Count == 0 && dirtyOutputs.Count == 0)
                {
                    return Enumerable.Empty<string>();
                }
                // Update the cache before Pass #2
                _cachedDependencyGraphSpec = dependencyGraphSpec;

                // Pass #2 For any dirty specs discrepancies, mark them and their parents as needing restore.
                var dirtyProjects = GetAllDirtyParents(dirtySpecs, dependencyGraphSpec);

                // All dirty projects + projects with outputs that need to be restored.
                return dirtyProjects.Union(dirtyOutputs);
            }
            else
            {
                _cachedDependencyGraphSpec = dependencyGraphSpec;

                return dependencyGraphSpec.Restore;
            }
        }

        internal static IList<string> GetAllDirtyParents(List<string> DirtySpecs, DependencyGraphSpec dependencyGraphSpec)
        {
            var projectsByUniqueName = dependencyGraphSpec.Projects
                .ToDictionary(t => t.RestoreMetadata.ProjectUniqueName, t => t, PathUtility.GetStringComparerBasedOnOS());

            var DirtyProjects = new HashSet<string>(DirtySpecs, PathUtility.GetStringComparerBasedOnOS());

            var sortedProjects = DependencyGraphSpec.SortPackagesByDependencyOrder(dependencyGraphSpec.Projects);

            foreach (var project in sortedProjects)
            {
                if (!DirtyProjects.Contains(project.RestoreMetadata.ProjectUniqueName))
                {
                    var projectReferences = GetPackageSpecDependencyIds(project);

                    foreach (var projectReference in projectReferences)
                    {
                        if (DirtyProjects.Contains(projectReference))
                        {
                            DirtyProjects.Add(project.RestoreMetadata.ProjectUniqueName);
                        }
                    }
                }
            }
            return DirtyProjects.ToList();
        }

        private static string[] GetPackageSpecDependencyIds(PackageSpec spec)
        {
            return spec.RestoreMetadata
                .TargetFrameworks
                .SelectMany(r => r.ProjectReferences)
                .Select(r => r.ProjectUniqueName)
                .Distinct(PathUtility.GetStringComparerBasedOnOS())
                .ToArray();
        }

        internal static IList<string> GetAllDirtyParentsFaster(List<string> DirtySpecs, DependencyGraphSpec dependencyGraphSpec)
        {
            var projectsByUniqueName = dependencyGraphSpec.Projects
                .ToDictionary(t => t.RestoreMetadata.ProjectUniqueName, t => t, PathUtility.GetStringComparerBasedOnOS());

            var DirtyProjects = new List<string>();

            var added = new SortedSet<string>(PathUtility.GetStringComparerBasedOnOS());
            var toWalk = new Stack<string>(DirtySpecs);

            while (toWalk.Count > 0)
            {
                var spec = toWalk.Pop();

                if (spec != null)
                {
                    DirtyProjects.Add(spec);

                    //// Find children
                    //foreach (var projectName in GetProjectReferenceNames(spec, projectsByUniqueName))
                    //{
                    //    if (added.Add(projectName))
                    //    {
                    //        toWalk.Push(GetProjectSpec(projectName));
                    //    }
                    //}
                }
            }

            return DirtyProjects;
        }

        internal static void GetOutputFilePaths(PackageSpec packageSpec, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath)
        {
            // TODO NK - account for project.json
            assetsFilePath = GetAssetsFilePath(packageSpec.RestoreMetadata.OutputPath);
            targetsFilePath = BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(packageSpec, BuildAssetsUtils.TargetsExtension);
            propsFilePath = BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(packageSpec, BuildAssetsUtils.PropsExtension);
            if (packageSpec.RestoreMetadata.RestoreLockProperties != null)
            {
                lockFilePath = packageSpec.RestoreMetadata.RestoreLockProperties.NuGetLockFilePath;
            }
            else
            {
                lockFilePath = null;
            }
        }

        private static bool AreOutputsUpToDate(string assetsFilePath, string targetsFilePath, string propsFilePath, string lockFilePath, OutputWriteTime outputWriteTime)
        {
            DateTime currentAssetsFileWriteTime = GetLastWriteTime(assetsFilePath);
            DateTime currentTargetsFilePath = GetLastWriteTime(targetsFilePath);
            DateTime currentPropsFilePath = GetLastWriteTime(propsFilePath);
            DateTime currentLockFilePath = GetLastWriteTime(lockFilePath);

            return outputWriteTime._lastAssetsFileWriteTime.Equals(currentAssetsFileWriteTime) &&
                   outputWriteTime._lastTargetsFileWriteTime.Equals(currentTargetsFilePath) &&
                   outputWriteTime._lastPropsFileWriteTime.Equals(currentPropsFilePath) &&
                   outputWriteTime._lastLockFileWriteTime.Equals(currentLockFilePath);
        }

        private static DateTime GetLastWriteTime(string assetsFilePath)
        {
            if (!string.IsNullOrWhiteSpace(assetsFilePath))
            {
                var fileInfo = new FileInfo(assetsFilePath);
                if (fileInfo.Exists)
                {
                    return fileInfo.LastWriteTimeUtc;
                }
            }
            return default;
        }

        private static string GetAssetsFilePath(string outputPath)
        {
            return Path.Combine(
                outputPath,
                LockFileFormat.AssetsFileName);
        }
    }

    internal struct OutputWriteTime
    {
        internal DateTime _lastAssetsFileWriteTime;
        internal DateTime _lastTargetsFileWriteTime;
        internal DateTime _lastPropsFileWriteTime;
        internal DateTime _lastLockFileWriteTime;
    }
}
