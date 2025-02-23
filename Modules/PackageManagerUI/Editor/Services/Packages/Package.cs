// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityEditor.PackageManager.UI
{
    // History of a single package
    internal class Package : IEquatable<Package>
    {
        public static bool ShouldProposeLatestVersions
        {
            get
            {
                // Until we figure out a way to test this properly, alway show standard behavior
                //    return InternalEditorUtility.IsUnityBeta() && !Unsupported.IsDeveloperMode();
                return false;
            }
        }

        // There can only be one package operation.
        private static IBaseOperation packageOperationInstance;

        public static bool PackageOperationInProgress
        {
            get { return packageOperationInstance != null && !packageOperationInstance.IsCompleted; }
        }

        internal const string packageManagerUIName = "com.unity.package-manager-ui";
        private readonly string packageName;
        private readonly IEnumerable<PackageInfo> source;

        internal Package(string packageName, IEnumerable<PackageInfo> infos)
        {
            if (string.IsNullOrEmpty(packageName))
                throw new ArgumentException("Cannot be empty or null", "packageName");

            if (!infos.Any())
                throw new ArgumentException("Cannot be empty", "infos");

            this.packageName = packageName;
            source = infos;
        }

        public Error Error { get; set; }

        public bool IsDiscoverable { get { return Versions.Any(p => p.IsDiscoverable); } }

        public PackageInfo Current { get { return Versions.FirstOrDefault(package => package.IsInstalled); } }

        // This is the latest verified or official release (eg: 1.3.2). Not necessarily the latest verified release (eg: 1.2.4) or that latest candidate (eg: 1.4.0-beta)
        public PackageInfo LatestUpdate
        {
            get
            {
                // We want to show the absolute latest when in beta mode
                if (ShouldProposeLatestVersions)
                    return Latest;

                // Override with current when it's version locked
                var current = Current;
                if (current != null && current.IsVersionLocked)
                    return current;

                // Get all the candidates versions (verified, release, preview) that are newer than current
                var verified = Verified;
                var latestRelease = LatestRelease;
                var latestPreview = Versions.LastOrDefault(package => package.IsPreview);
                var candidates = new List<PackageInfo>
                {
                    verified,
                    latestRelease,
                    latestPreview,
                }.Where(package => package != null && (current == null || current == package || current.Version < package.Version)).ToList();

                if (candidates.Contains(verified))
                    return verified;
                if ((current == null || !current.IsVerified) && candidates.Contains(latestRelease))
                    return latestRelease;
                if ((current == null || current.IsPreview) && candidates.Contains(latestPreview))
                    return latestPreview;

                // Show current if it exists, otherwise latest user visible, and then otherwise show the absolute latest
                return current ?? Latest;
            }
        }

        public PackageInfo LatestPatch
        {
            get
            {
                if (Current == null)
                    return null;

                // Get all version that have the same Major/Minor
                var versions = Versions.Where(package => package.Version.Major == Current.Version.Major && package.Version.Minor == Current.Version.Minor);

                return versions.LastOrDefault();
            }
        }

        // This is the very latest version, including pre-releases (eg: 1.4.0-beta).
        internal PackageInfo Latest { get { return Versions.FirstOrDefault(package => package.IsLatest) ?? Versions.LastOrDefault(); } }

        // Returns the current version if it exist, otherwise returns the latest user visible version.
        internal PackageInfo VersionToDisplay { get { return Current ?? LatestUpdate; } }

        // Every version available for this package
        internal IEnumerable<PackageInfo> Versions { get { return source.OrderBy(package => package.Version); } }

        // Every version that's not a pre-release (eg: not beta/alpha/preview).
        internal IEnumerable<PackageInfo> ReleaseVersions
        {
            get { return Versions.Where(package => !package.IsPreRelease); }
        }

        internal IEnumerable<PackageInfo> KeyVersions
        {
            get
            {
                //
                // Get key versions -- Latest, Verified, LatestPatch, Current.
                var keyVersions = new HashSet<PackageInfo>();
                if (LatestRelease != null) keyVersions.Add(LatestRelease);
                if (Current != null) keyVersions.Add(Current);
                if (Verified != null && Verified != Current) keyVersions.Add(Verified);
                if (LatestPatch != null && IsAfterCurrentVersion(LatestPatch)) keyVersions.Add(LatestPatch);
                if (Current == null && LatestRelease == null && Latest != null) keyVersions.Add(Latest);
                if (ShouldProposeLatestVersions && Latest != LatestRelease && Latest != null) keyVersions.Add(Latest);
                keyVersions.Add(LatestUpdate);        // Make sure LatestUpdate is always in the list.

                return keyVersions.OrderBy(package => package.Version);
            }
        }
        internal PackageInfo LatestRelease { get {return ReleaseVersions.LastOrDefault();}}
        internal PackageInfo Verified { get {return Versions.FirstOrDefault(package => package.IsVerified);}}

        internal bool IsAfterCurrentVersion(PackageInfo packageInfo) { return Current == null || (packageInfo != null  && packageInfo.Version > Current.Version); }

        internal bool IsBuiltIn {get { return Versions.Any() && Versions.First().IsBuiltIn; }}

        public string Name { get { return packageName; } }

        public bool IsPackageManagerUI
        {
            get { return Name == packageManagerUIName; }
        }

        public bool AnyInDevelopment
        {
            get { return Versions.Any(info => info.IsInDevelopment); }
        }

        public bool Equals(Package other)
        {
            if (other == null)
                return false;

            return packageName == other.packageName;
        }

        public override int GetHashCode()
        {
            return packageName.GetHashCode();
        }

        [SerializeField]
        internal readonly OperationSignal<IAddOperation> AddSignal = new OperationSignal<IAddOperation>();

        private Action OnAddOperationFinalizedEvent;

        internal void Add(PackageInfo packageInfo)
        {
            if (packageInfo == Current || PackageOperationInProgress)
                return;

            var operation = OperationFactory.Instance.CreateAddOperation();
            packageOperationInstance = operation;
            OnAddOperationFinalizedEvent = () =>
            {
                AddSignal.Operation = null;
                operation.OnOperationFinalized -= OnAddOperationFinalizedEvent;
                PackageManagerWindow.FetchListOfflineCacheForAllWindows();
            };

            operation.OnOperationFinalized += OnAddOperationFinalizedEvent;

            AddSignal.SetOperation(operation);
            operation.AddPackageAsync(packageInfo);
        }

        internal void Update()
        {
            Add(Latest);
        }

        internal static void AddFromUrl(string url)
        {
            if (PackageOperationInProgress)
                return;
            var operation = OperationFactory.Instance.CreateAddOperation();
            packageOperationInstance = operation;
            // convert SCP-like syntax to SSH URL as currently UPM doesn't support it
            if (url.ToLower().StartsWith("git@"))
                url = "ssh://" + url.Replace(':', '/');
            operation.AddPackageAsync(url);
        }

        internal static void AddFromLocalDisk(string path)
        {
            if (PackageOperationInProgress)
                return;

            var packageJson = PackageJsonHelper.Load(path);
            if (null == packageJson)
            {
                Debug.LogError(string.Format("Invalid package path: cannot find \"{0}\".", path));
                return;
            }

            var operation = OperationFactory.Instance.CreateAddOperation();
            packageOperationInstance = operation;
            operation.AddPackageAsync(packageJson.PackageInfo);
        }

        [SerializeField]
        internal readonly OperationSignal<IRemoveOperation> RemoveSignal = new OperationSignal<IRemoveOperation>();

        private Action OnRemoveOperationFinalizedEvent;

        public void Remove()
        {
            if (Current == null || PackageOperationInProgress)
                return;

            var operation = OperationFactory.Instance.CreateRemoveOperation();
            packageOperationInstance = operation;
            OnRemoveOperationFinalizedEvent = () =>
            {
                RemoveSignal.Operation = null;
                operation.OnOperationFinalized -= OnRemoveOperationFinalizedEvent;
                PackageManagerWindow.FetchListOfflineCacheForAllWindows();
            };

            operation.OnOperationFinalized += OnRemoveOperationFinalizedEvent;
            RemoveSignal.SetOperation(operation);

            operation.RemovePackageAsync(Current);
        }

        [SerializeField]
        internal readonly OperationSignal<IEmbedOperation> EmbedSignal = new OperationSignal<IEmbedOperation>();

        private Action OnEmbedOperationFinalizedEvent;
        private Action<PackageInfo> OnEmbedOperationSuccessEvent;

        public void Embed(Action<PackageInfo> onSuccess = null)
        {
            if (Current == null || PackageOperationInProgress)
                return;

            var operation = OperationFactory.Instance.CreateEmbedOperation();
            packageOperationInstance = operation;
            OnEmbedOperationFinalizedEvent = () =>
            {
                EmbedSignal.Operation = null;
                operation.OnOperationFinalized -= OnEmbedOperationFinalizedEvent;
            };

            OnEmbedOperationSuccessEvent = info =>
            {
                onSuccess?.Invoke(info);
                operation.OnOperationSuccess -= OnEmbedOperationSuccessEvent;
            };

            operation.OnOperationSuccess += OnEmbedOperationSuccessEvent;
            operation.OnOperationFinalized += OnEmbedOperationFinalizedEvent;
            EmbedSignal.SetOperation(operation);

            operation.EmbedPackageAsync(Current);
        }
    }
}
