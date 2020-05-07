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

        public IEnumerable<string> PerformUpToDateCheck(DependencyGraphSpec dependencyGraphSpec)
        {
            var result = Enumerable.Empty<string>();
            if (_cachedDependencyGraphSpec != null)
            {
                var DirtySpecs = new List<string>();
                var DirtyOutputs = new List<string>();

                // Pass #1. Validate all the graph specs. If all are up to date. We are good!
                foreach (var project in dependencyGraphSpec.Projects)
                {
                    var projectUniqueName = project.RestoreMetadata.ProjectUniqueName;
                    // Check the cached one.
                    var cache = _cachedDependencyGraphSpec.GetProjectSpec(projectUniqueName);
                    if (cache == null || !project.Equals(cache))
                    {
                        DirtySpecs.Add(projectUniqueName);
                    }
                    // TODO NK - Handle project.json
                    if (project.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference)
                    {
                        if (_outputWriteTimes.TryGetValue(projectUniqueName, out OutputWriteTime outputWriteTime))
                        {
                            GetOutputFilePaths(project, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath);
                            if (!AreOutputsUpToDate(assetsFilePath, targetsFilePath, propsFilePath, lockFilePath, outputWriteTime))
                            {
                                DirtyOutputs.Add(projectUniqueName);
                            }
                        }
                        else
                        {
                            DirtyOutputs.Add(projectUniqueName);
                        }
                    }
                }

                if (DirtySpecs.Count == 0 && DirtyOutputs.Count == 0)
                {
                    // Fast path. No-Op completely if all specs are up to date.
                    return result;
                }

                // Pass #2 For any dirty specs discrepancies, mark them and their parents as needing restore.
                var dirtyProjects = GetAllDirtyParents(DirtySpecs, dependencyGraphSpec);

                // Pass #3 All dirty projects + projects with outputs need to be restored.

                result = dirtyProjects.Union(DirtyOutputs);
            }

            // Update the cache.
            _cachedDependencyGraphSpec = dependencyGraphSpec;

            return result;
        }

        public IList<string> GetAllDirtyParents(List<string> DirtySpecs, DependencyGraphSpec dependencyGraphSpec)
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

        public IList<string> GetAllDirtyParentsFaster(List<string> DirtySpecs, DependencyGraphSpec dependencyGraphSpec)
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

        //public IReadOnlyList<string> GetParents(string rootUniqueName, DependencyGraphSpec dependencyGraphSpec)
        //{
        //    var parents = new List<PackageSpec>();

        //    foreach (var project in dependencyGraphSpec.Projects)
        //    {
        //        if (!StringComparer.OrdinalIgnoreCase.Equals(
        //            project.RestoreMetadata.ProjectUniqueName,
        //            rootUniqueName))
        //        {
        //            var closure = GetClosure(project.RestoreMetadata.ProjectUniqueName);

        //            if (closure.Any(e => StringComparer.OrdinalIgnoreCase.Equals(
        //                e.RestoreMetadata.ProjectUniqueName,
        //                rootUniqueName)))
        //            {
        //                parents.Add(project);
        //            }
        //        }
        //    }

        //    return parents
        //        .Select(e => e.RestoreMetadata.ProjectUniqueName)
        //        .ToList();
        //}

        private static void GetOutputFilePaths(PackageSpec packageSpec, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath)
        {
            assetsFilePath = GetAssetsFilePath(packageSpec.RestoreMetadata.OutputPath);
            targetsFilePath = BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(packageSpec, BuildAssetsUtils.TargetsExtension);
            propsFilePath = BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(packageSpec, BuildAssetsUtils.PropsExtension);
            lockFilePath = null; // fix the lock files later.
        }

        private bool AreOutputsUpToDate(string assetsFilePath, string targetsFilePath, string propsFilePath, string lockFilePath, OutputWriteTime outputWriteTime)
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

        public void ReportStatus(IReadOnlyList<RestoreSummary> restoreSummaries)
        {
            _failedProjects.Clear();

            foreach (var summary in restoreSummaries)
            {
                if (summary.Success)
                {
                    var packageSpec = _cachedDependencyGraphSpec.GetProjectSpec(summary.InputPath);
                    GetOutputFilePaths(packageSpec, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath);

                    _outputWriteTimes.Add(summary.InputPath, new OutputWriteTime()
                    {
                        _lastAssetsFileWriteTime = GetLastWriteTime(assetsFilePath),
                        _lastTargetsFileWriteTime = GetLastWriteTime(targetsFilePath),
                        _lastPropsFileWriteTime = GetLastWriteTime(propsFilePath),
                        _lastLockFileWriteTime = GetLastWriteTime(lockFilePath)
                    });
                }
                else
                {
                    _failedProjects.Add(summary.InputPath);
                }

            }
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
