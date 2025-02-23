// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Modules;
using UnityEditor.Utils;
using UnityEditorInternal;
using UnityEngine;

internal abstract class DesktopStandalonePostProcessor : DefaultBuildPostprocessor
{
    readonly bool m_HasIl2CppPlayers;
    protected const string k_MonoDirectoryName = "MonoBleedingEdge";

    protected DesktopStandalonePostProcessor(bool hasIl2CppPlayers)
    {
        m_HasIl2CppPlayers = hasIl2CppPlayers;
    }

    public override string PrepareForBuild(BuildOptions options, BuildTarget target)
    {
        if (!m_HasIl2CppPlayers)
        {
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
            if (PlayerSettings.GetScriptingBackend(buildTargetGroup) == ScriptingImplementation.IL2CPP)
                return "Currently selected scripting backend (IL2CPP) is not installed.";
        }

        return null;
    }

    public override void PostProcess(BuildPostProcessArgs args)
    {
        try
        {
            CheckSafeProjectOverwrite(args);

            var filesToNotOverwrite = new HashSet<string>(new FilePathComparer());
            SetupStagingArea(args, filesToNotOverwrite);

            if (EditorUtility.DisplayCancelableProgressBar("Building Player", "Copying files to final destination", 0.1f))
                throw new OperationCanceledException();

            if (GetInstallingIntoBuildsFolder(args))
                CopyStagingAreaIntoBuildsFolder(args);
            else
                CopyStagingAreaIntoDestination(args, filesToNotOverwrite);

            ProcessSymbolFiles(args);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new BuildFailedException(e);
        }
    }

    protected virtual void CheckSafeProjectOverwrite(BuildPostProcessArgs args)
    {
    }

    public override bool SupportsLz4Compression()
    {
        return true;
    }

    public override void UpdateBootConfig(BuildTarget target, BootConfigData config, BuildOptions options)
    {
        base.UpdateBootConfig(target, config, options);

        if (PlayerSettings.forceSingleInstance)
            config.AddKey("single-instance");
        if (!PlayerSettings.useFlipModelSwapchain)
            config.AddKey("force-d3d11-bltblt-mode");
        if (IL2CPPUtils.UseIl2CppCodegenWithMonoBackend(BuildPipeline.GetBuildTargetGroup(target)))
            config.Set("mono-codegen", "il2cpp");
        if ((options & BuildOptions.EnableHeadlessMode) != 0)
            config.AddKey("headless");
        if ((options & BuildOptions.EnableCodeCoverage) != 0)
            config.Set("enableCodeCoverage", "1");
        if (!PlayerSettings.usePlayerLog)
            config.AddKey("nolog");
    }

    private void CopyNativePlugins(BuildPostProcessArgs args, out List<string> cppPlugins)
    {
        string buildTargetName = BuildPipeline.GetBuildTargetName(args.target);
        IPluginImporterExtension pluginImpExtension = new DesktopPluginImporterExtension();

        string pluginsFolder = GetStagingAreaPluginsFolder(args);
        string subDir32Bit = Path.Combine(pluginsFolder, "x86");
        string subDir64Bit = Path.Combine(pluginsFolder, "x86_64");

        bool haveCreatedPluginsFolder = false;
        bool haveCreatedSubDir32Bit = false;
        bool haveCreatedSubDir64Bit = false;
        cppPlugins = new List<string>();

        foreach (PluginImporter imp in PluginImporter.GetImporters(args.target))
        {
            BuildTarget t = args.target;

            // Skip .cpp files. They get copied to il2cpp output folder just before code compilation
            if (DesktopPluginImporterExtension.IsCppPluginFile(imp.assetPath))
            {
                cppPlugins.Add(imp.assetPath);
                continue;
            }

            // Skip managed DLLs.
            if (!imp.isNativePlugin)
                continue;

            // HACK: This should never happen.
            if (string.IsNullOrEmpty(imp.assetPath))
            {
                UnityEngine.Debug.LogWarning("Got empty plugin importer path for " + args.target.ToString());
                continue;
            }

            if (!haveCreatedPluginsFolder)
            {
                Directory.CreateDirectory(pluginsFolder);
                haveCreatedPluginsFolder = true;
            }

            bool isDirectory = Directory.Exists(imp.assetPath);
            string cpu = imp.GetPlatformData(t, "CPU");
            switch (cpu)
            {
                case "x86":
                    if (t == BuildTarget.StandaloneWindows64 ||
                        t == BuildTarget.StandaloneLinux64)
                    {
                        continue;
                    }
                    if (!haveCreatedSubDir32Bit)
                    {
                        Directory.CreateDirectory(subDir32Bit);
                        haveCreatedSubDir32Bit = true;
                    }
                    break;
                case "x86_64":
                    if (t != BuildTarget.StandaloneOSX &&
                        t != BuildTarget.StandaloneWindows64 &&
                        t != BuildTarget.StandaloneLinux64)
                    {
                        continue;
                    }
                    if (!haveCreatedSubDir64Bit)
                    {
                        Directory.CreateDirectory(subDir64Bit);
                        haveCreatedSubDir64Bit = true;
                    }
                    break;
                // This is a special case for CPU targets, means no valid CPU is selected
                case "None":
                    continue;
            }

            string destinationPath = pluginImpExtension.CalculateFinalPluginPath(buildTargetName, imp);
            if (string.IsNullOrEmpty(destinationPath))
                continue;

            string finalDestinationPath = Path.Combine(pluginsFolder, destinationPath);

            if (isDirectory)
            {
                FileUtil.CopyDirectoryRecursive(imp.assetPath, finalDestinationPath);
            }
            else
            {
                FileUtil.UnityFileCopy(imp.assetPath, finalDestinationPath);
            }
        }

        // TODO: Move all plugins using GetExtensionPlugins to GetImporters and remove GetExtensionPlugins
        foreach (UnityEditorInternal.PluginDesc pluginDesc in PluginImporter.GetExtensionPlugins(args.target))
        {
            if (!haveCreatedPluginsFolder)
            {
                Directory.CreateDirectory(pluginsFolder);
                haveCreatedPluginsFolder = true;
            }

            string pluginCopyPath = Path.Combine(pluginsFolder, Path.GetFileName(pluginDesc.pluginPath));

            // Plugins copied through GetImporters take priority, don't overwrite
            if (Directory.Exists(pluginCopyPath) || File.Exists(pluginCopyPath))
                continue;

            if (Directory.Exists(pluginDesc.pluginPath))
            {
                FileUtil.CopyDirectoryRecursive(pluginDesc.pluginPath, pluginCopyPath);
            }
            else
            {
                FileUtil.CopyFileIfExists(pluginDesc.pluginPath, pluginCopyPath, false);
            }
        }
    }

    private void CopyCppPlugins(BuildPostProcessArgs args, string cppOutputDir, IEnumerable<string> cppPlugins)
    {
        if (GetCreateSolution(args))
            return;

        foreach (var plugin in cppPlugins)
        {
            FileUtil.CopyFileOrDirectory(plugin, Path.Combine(cppOutputDir, Path.GetFileName(plugin)));
        }
    }

    private void SetupStagingArea(BuildPostProcessArgs args, HashSet<string> filesToNotOverwrite)
    {
        List<string> cppPlugins;
        CopyNativePlugins(args, out cppPlugins);

        CreateApplicationData(args);

        PostprocessBuildPlayer.InstallStreamingAssets(args.stagingAreaData, args.report);

        if (GetInstallingIntoBuildsFolder(args))
        {
            CopyDataForBuildsFolder(args);
        }
        else
        {
            CopyVariationFolderIntoStagingArea(args);

            if (GetCreateSolution(args))
                CopyPlayerSolutionIntoStagingArea(args, filesToNotOverwrite);

            if (UseIl2Cpp)
            {
                var il2cppPlatformProvider = GetPlatformProvider(args);
                IL2CPPUtils.RunIl2Cpp(args.stagingAreaData, il2cppPlatformProvider, (cppOutputDir) => CopyCppPlugins(args, cppOutputDir, cppPlugins), args.usedClassRegistry);

                if (GetCreateSolution(args))
                {
                    ProcessIl2CppOutputForSolution(args, il2cppPlatformProvider, cppPlugins);
                }
                else
                {
                    ProcessIl2CppOutputForBinary(args);
                }
            }
            else
            {
                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(args.target);
                var managedStrippingLevel = PlayerSettings.GetManagedStrippingLevel(buildTargetGroup);
                AssemblyStripper.StripForMonoBackend(args.target, args.usedClassRegistry, managedStrippingLevel, args.report);
            }

            RenameFilesInStagingArea(args);
        }
    }

    private void ProcessIl2CppOutputForBinary(BuildPostProcessArgs args)
    {
        // Move GameAssembly next to game executable
        var il2cppOutputNativeDirectory = Path.Combine(args.stagingAreaData, "Native");
        var gameAssemblyDirectory = GetDirectoryForGameAssembly(args);
        foreach (var file in Directory.GetFiles(il2cppOutputNativeDirectory))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("."))
                continue; // Skip files starting with ., as they weren't output by our tools and potentially belong to the OS (like .DS_Store on macOS)

            FileUtil.MoveFileOrDirectory(file, Path.Combine(gameAssemblyDirectory, fileName));
        }

        // Move il2cpp data directory one directory up
        FileUtil.MoveFileOrDirectory(Path.Combine(il2cppOutputNativeDirectory, "Data"), Path.Combine(args.stagingAreaData, "il2cpp_data"));

        // Native directory is supposed to be empty at this point
        FileUtil.DeleteFileOrDirectory(il2cppOutputNativeDirectory);

        var dataBackupFolder = Path.Combine(args.stagingArea, GetIl2CppDataBackupFolderName(args));
        FileUtil.CreateOrCleanDirectory(dataBackupFolder);

        // Move generated C++ code out of Data directory
        FileUtil.MoveFileOrDirectory(Path.Combine(args.stagingAreaData, "il2cppOutput"), Path.Combine(dataBackupFolder, "il2cppOutput"));

        if (IL2CPPUtils.UseIl2CppCodegenWithMonoBackend(BuildPipeline.GetBuildTargetGroup(args.target)))
        {
            // Are we using IL2CPP code generation with the Mono runtime? If so, strip the assemblies so we can use them for metadata.
            StripAssembliesToLeaveOnlyMetadata(args.target, args.stagingAreaDataManaged);
        }
        else
        {
            // Otherwise, move them to temp data directory as il2cpp does not need managed assemblies to run
            FileUtil.MoveFileOrDirectory(args.stagingAreaDataManaged, Path.Combine(dataBackupFolder, "Managed"));
        }

        ProcessPlatformSpecificIL2CPPOutput(args);
    }

    protected virtual void ProcessIl2CppOutputForSolution(BuildPostProcessArgs args, IIl2CppPlatformProvider il2cppPlatformProvider, IEnumerable<string> cppPlugins)
    {
        throw new NotSupportedException("CreateSolution is not supported on " + BuildPipeline.GetBuildTargetName(args.target));
    }

    private static void StripAssembliesToLeaveOnlyMetadata(BuildTarget target, string stagingAreaDataManaged)
    {
        var checker = new AssemblyReferenceChecker();
        checker.CollectReferences(stagingAreaDataManaged, true, 0.0f, false);

        EditorUtility.DisplayProgressBar("Removing bytecode from assemblies", "Stripping assemblies so that only metadata remains", 0.95F);
        MonoAssemblyStripping.MonoCilStrip(target, stagingAreaDataManaged, checker.GetAssemblyFileNames());
    }

    // Creates app.info which is used by Standalone player (when run in Low Integrity mode) when creating log file path at program start.
    // Log file path is created very early in the program execution, when none of the Unity managers are even created, that's why we can't get it from PlayerSettings
    // Note: In low integrity mode, we can only write to %USER PROFILE%\AppData\LocalLow on Windows
    private static void CreateApplicationData(BuildPostProcessArgs args)
    {
        File.WriteAllText(Path.Combine(args.stagingAreaData, "app.info"),
            string.Join("\n", new[]
            {
                args.companyName,
                args.productName
            }));
        args.report.RecordFileAdded(Path.Combine(args.stagingAreaData, "app.info"), CommonRoles.appInfo);
    }

    protected bool CopyPlayerFilter(string path, BuildPostProcessArgs args)
    {
        // Don't copy UnityEngine mdb files
        return Path.GetExtension(path) != ".mdb" || !Path.GetFileName(path).StartsWith("UnityEngine.");
    }

    private static uint StringToFourCC(string literal)
    {
        if (literal.Length > 4)
            throw new NotSupportedException("FourCC can consist of maximum 4 characters");

        uint result = 0;
        foreach (var c in literal)
            result = (result << 8) + (byte)c;

        return result;
    }

    protected static void CopyResolutionDialogBanner(string destinationFolder)
    {
#pragma warning disable 618
        var bannerTexture = PlayerSettings.resolutionDialogBanner;
        if (bannerTexture != null)
        {
            var path = Path.Combine(destinationFolder, "ScreenSelector.png");
            IconUtility.SaveTextureToFile(path, bannerTexture, StringToFourCC("PNGf"));
        }
#pragma warning restore 618
    }

    protected virtual void CopyPlayerSolutionIntoStagingArea(BuildPostProcessArgs args, HashSet<string> filesToNotOverwrite)
    {
        throw new NotSupportedException("CreateSolution is not supported on " + BuildPipeline.GetBuildTargetName(args.target));
    }

    protected string GetVariationFolder(BuildPostProcessArgs args) =>
        Paths.Combine(args.playerPackage, "Variations", GetVariationName(args));

    protected abstract void CopyVariationFolderIntoStagingArea(BuildPostProcessArgs args);

    protected static void RecordCommonFiles(BuildPostProcessArgs args, string variationSourceFolder, string monoFolderRoot)
    {
        // Mark all the managed DLLs in Data/Managed as engine API assemblies
        // Data/Managed may already contain managed DLLs in the UnityEngine.*.dll naming scheme from the extensions
        // So we find the files in the source Variations directory and mark the corresponding files in the output
        var path = Path.Combine(variationSourceFolder, "Data/Managed");
        foreach (var file in Directory.GetFiles(path, "*.dll")
                 .Concat(Directory.GetFiles(path, "*.dll.mdb")))
        {
            var filename = Path.GetFileName(file);
            if (!filename.StartsWith("UnityEngine"))
                continue;

            var targetFilePath = Path.Combine(args.stagingArea, "Data/Managed/" + filename);
            args.report.RecordFileAdded(targetFilePath, CommonRoles.managedEngineApi);
        }

        // Mark the default resources file
        args.report.RecordFileAdded(Path.Combine(args.stagingArea, "Data/Resources/unity default resources"),
            CommonRoles.builtInResources);

        // Mark up each mono runtime
        args.report.RecordFilesAddedRecursive(Path.Combine(monoFolderRoot, k_MonoDirectoryName + "/EmbedRuntime"),
            CommonRoles.monoRuntime);
        args.report.RecordFilesAddedRecursive(Path.Combine(monoFolderRoot, k_MonoDirectoryName + "/etc"),
            CommonRoles.monoConfig);
    }

    private void CopyStagingAreaIntoBuildsFolder(BuildPostProcessArgs args)
    {
        var dst = GetDestinationFolderForInstallingIntoBuildsFolder(args);

        if (!Directory.Exists(Path.GetDirectoryName(dst)))
        {
            throw new Exception("Installing in builds folder failed because the player has not been built (You most likely want to enable 'Development build').");
        }

        FileUtil.CopyDirectoryRecursive(args.stagingAreaData, dst, overwrite: true);
    }

    private void CopyStagingAreaIntoDestination(BuildPostProcessArgs args, HashSet<string> filesToNotOverwrite)
    {
        DeleteDestination(args);

        // Copy entire stagingarea over
        CopyFilesToDestination(args.stagingArea, GetDestinationFolder(args), filesToNotOverwrite);
        args.report.RecordFilesMoved(args.stagingArea, GetDestinationFolder(args));
    }

    private static void CopyFilesToDestination(string source, string target, HashSet<string> filesToNotOverwrite)
    {
        bool createDirectory = !Directory.Exists(target);
        foreach (string sourceFile in Directory.GetFiles(source))
        {
            if (createDirectory)
            {
                Directory.CreateDirectory(target);
                createDirectory = false;
            }

            var targetFile = Path.Combine(target, Path.GetFileName(sourceFile));

            if (File.Exists(targetFile))
            {
                if (filesToNotOverwrite.Contains(sourceFile))
                    continue;

                FileUtil.DeleteFileOrDirectory(targetFile);
            }

            FileUtil.MoveFileOrDirectory(sourceFile, targetFile);
        }

        foreach (var directory in Directory.GetDirectories(source))
            CopyFilesToDestination(directory, Path.Combine(target, Path.GetFileName(directory)), filesToNotOverwrite);
    }

    protected abstract string GetStagingAreaPluginsFolder(BuildPostProcessArgs args);

    protected abstract void DeleteDestination(BuildPostProcessArgs args);

    protected static bool UseMono => !UseIl2Cpp || IL2CPPUtils.UseIl2CppCodegenWithMonoBackend(BuildTargetGroup.Standalone);

    protected static void DeleteUnusedMono(string dataFolder, BuildReport report)
    {
        // Mono is built by the il2cpp builder, so we dont need the libs copied
        if (!UseMono)
        {
            var monoPath = Path.Combine(dataFolder, k_MonoDirectoryName);
            FileUtil.DeleteFileOrDirectory(monoPath);
            report.RecordFilesDeletedRecursive(monoPath);
        }
    }

    protected abstract string GetDestinationFolderForInstallingIntoBuildsFolder(BuildPostProcessArgs args);

    protected abstract void CopyDataForBuildsFolder(BuildPostProcessArgs args);

    protected static bool GetInstallingIntoBuildsFolder(BuildPostProcessArgs args)
    {
        return (args.options & BuildOptions.InstallInBuildFolder) != 0;
    }

    protected static bool UseIl2Cpp => PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) == ScriptingImplementation.IL2CPP;

    protected virtual bool GetCreateSolution(BuildPostProcessArgs args) { return false; }

    protected virtual string GetDestinationFolder(BuildPostProcessArgs args)
    {
        return FileUtil.UnityGetDirectoryName(args.installPath);
    }

    protected static bool GetDevelopment(BuildPostProcessArgs args)
    {
        return ((args.options & BuildOptions.Development) != 0);
    }

    protected static bool IsHeadlessMode(BuildPostProcessArgs args)
    {
        return ((args.options & BuildOptions.EnableHeadlessMode) != 0);
    }

    protected virtual string GetVariationName(BuildPostProcessArgs args)
    {
        return string.Format("{0}_{1}",
            PlatformStringFor(args.target),
            (GetDevelopment(args) ? "development" : "nondevelopment"));
    }

    protected static string GetPathSafeProductName(BuildPostProcessArgs args)
    {
        return Paths.MakeValidFileName(args.productName);
    }

    protected abstract string PlatformStringFor(BuildTarget target);

    protected abstract void RenameFilesInStagingArea(BuildPostProcessArgs args);

    protected abstract IIl2CppPlatformProvider GetPlatformProvider(BuildPostProcessArgs args);

    protected virtual void ProcessPlatformSpecificIL2CPPOutput(BuildPostProcessArgs args)
    {
    }

    protected virtual void ProcessSymbolFiles(BuildPostProcessArgs args)
    {
    }

    protected static string GetIl2CppDataBackupFolderName(BuildPostProcessArgs args)
    {
        return Path.GetFileNameWithoutExtension(args.installPath) + "_BackUpThisFolder_ButDontShipItWithYourGame";
    }

    protected virtual string GetDirectoryForGameAssembly(BuildPostProcessArgs args)
    {
        return args.stagingArea;
    }

    internal class ScriptingImplementations : DefaultScriptingImplementations
    {
    }

    private class FilePathComparer : IEqualityComparer<string>
    {
        public bool Equals(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(string path)
        {
            return Path.GetFullPath(path).ToLowerInvariant().GetHashCode();
        }
    }
}
