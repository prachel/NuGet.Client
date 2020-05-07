// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;

using NuGet.Commands;
using NuGet.ProjectModel;

namespace NuGet.SolutionRestoreManager
{
    public interface ISolutionRestoreChecker
    {
        IEnumerable<string> PerformUpToDateCheck(DependencyGraphSpec dependencyGraphSpec);
        void ReportStatus(IReadOnlyList<RestoreSummary> restoreSummaries);
    }
}
