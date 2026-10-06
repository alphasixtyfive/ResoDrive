# Microsoft PE debug directory type 20 contains extended DLL characteristics;
# bit 0 marks CET shadow-stack compatibility. Read the shipped file, never infer
# this from a requested MSBuild property. PEReader rejects malformed PE headers.
function Get-PeCetCompatibility([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = $null
    try {
        $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
        $entries = @($reader.ReadDebugDirectory() | Where-Object { [int]$_.Type -eq 20 })
        if ($entries.Count -eq 0) { return $false }
        if ($entries.Count -ne 1 -or $entries[0].DataSize -lt 4 -or $entries[0].DataPointer -lt 0 -or
            [long]$entries[0].DataPointer + 4 -gt $stream.Length) {
            throw 'Invalid extended DLL characteristics in the published PE.'
        }
        $stream.Position = $entries[0].DataPointer
        $flags = [byte[]]::new(4)
        $stream.ReadExactly($flags, 0, 4)
        return ([BitConverter]::ToUInt32($flags, 0) -band 1) -ne 0
    } finally { if ($null -ne $reader) { $reader.Dispose() }; $stream.Dispose() }
}

# Read assembly metadata without loading or executing the application. This
# verifies the updater's immutable variant flag in the actual compiled assembly.
function Get-ManagedCetCompatibility([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = $null
    try {
        $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
        $found = $false
        foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $metadata.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $member = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$member.Parent)
            if ($metadata.GetString($type.Name) -ne 'AssemblyMetadataAttribute' -or
                $metadata.GetString($type.Namespace) -ne 'System.Reflection') { continue }
            $blob = $metadata.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly metadata serialization.' }
            $key = $blob.ReadSerializedString()
            $value = $blob.ReadSerializedString()
            if ($key -ne 'ResoDriveCETCompatibility') { continue }
            if ($found -or $value -cne 'true') { throw 'Invalid or duplicate CET compatibility assembly metadata.' }
            $found = $true
        }
        return $found
    } finally { if ($null -ne $reader) { $reader.Dispose() }; $stream.Dispose() }
}

# Validate the actual cached MSI helper without executing or installing it.
# Supported Windows Installer APIs read only one fixed Binary table record.
function Export-MsiPreparationHelper([string]$MsiPath, [string]$OutputPath) {
    if (-not ('ResoDrivePackageBinaryReader' -as [type])) {
        Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class ResoDrivePackageBinaryReader {
    [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll")] static extern uint MsiRecordReadStream(uint record, uint field, byte[] buffer, ref uint count);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
    static void Check(uint result) { if(result != 0) throw new System.ComponentModel.Win32Exception((int)result); }
    public static void Export(string msi, string output) {
        uint database=0, view=0, record=0;
        try {
            Check(MsiOpenDatabaseW(msi, IntPtr.Zero, out database));
            Check(MsiDatabaseOpenViewW(database, "SELECT `Data` FROM `Binary` WHERE `Name` = 'ResoDriveInstallationHelper'", out view));
            Check(MsiViewExecute(view, 0));
            Check(MsiViewFetch(view, out record));
            using(var stream=new FileStream(output, FileMode.CreateNew, FileAccess.Write)) {
                var buffer=new byte[65536];
                for(;;) {
                    uint count=(uint)buffer.Length;
                    Check(MsiRecordReadStream(record, 1, buffer, ref count));
                    if(count==0) break;
                    if(stream.Length+count > 64*1024*1024) throw new InvalidDataException("MSI helper exceeded the bounded single-file application size.");
                    stream.Write(buffer,0,(int)count);
                }
                if(stream.Length==0) throw new InvalidDataException("MSI helper is empty.");
            }
        } finally {
            if(record!=0) MsiCloseHandle(record);
            if(view!=0) MsiCloseHandle(view);
            if(database!=0) MsiCloseHandle(database);
        }
    }
}
'@
    }
    [ResoDrivePackageBinaryReader]::Export([IO.Path]::GetFullPath($MsiPath), [IO.Path]::GetFullPath($OutputPath))
}
