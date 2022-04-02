// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceHub.Framework;
using NuGet.Versioning;
using NuGet.VisualStudio.Internal.Contracts;

namespace NuGet.PackageManagement.UI.Test
{
    /// <summary>
    /// An implementation of <see cref="DetailControlModel"/> to test its methods
    /// </summary>
    internal class TestDetailControlModel : DetailControlModel
    {
        public override bool IsSolution => throw new NotImplementedException();

        public TestDetailControlModel(IServiceBroker serviceBroker, IEnumerable<IProjectContextInfo> projects) : base(serviceBroker, projects)
        {
        }

        public override IEnumerable<IProjectContextInfo> GetSelectedProjects(UserAction action)
        {
            throw new NotImplementedException();
        }

        public override Task RefreshAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected override Task CreateVersionsAsync(CancellationToken cancellationToken)
        {
            _versions = new List<DisplayVersion>()
            {
                new DisplayVersion(NuGetVersion.Parse("1.0.0"), "additionalInfo")
,           };

            return Task.CompletedTask;
        }
    }
}
