using System;
using System.Reflection;

// Late-bound replacement for the IWshRuntimeLibrary COM reference.
// Generating the interop assembly needs tlbimp from the .NET Framework SDK,
// which makes the project unbuildable with just the .NET SDK + MSBuild Build Tools.
namespace IWshRuntimeLibrary
{
    public class WshShell
    {
        private static readonly Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
        private readonly object shell = Activator.CreateInstance(shellType);

        public IWshShortcut CreateShortcut(string pathLink)
        {
            var link = shellType.InvokeMember(nameof(CreateShortcut), BindingFlags.InvokeMethod, null, shell, new object[] { pathLink });
            return new IWshShortcut(link);
        }
    }

    public class IWshShortcut
    {
        private readonly object link;
        private readonly Type linkType;

        internal IWshShortcut(object link)
        {
            this.link = link;
            linkType = link.GetType();
        }

        private string Get(string name)
        {
            return linkType.InvokeMember(name, BindingFlags.GetProperty, null, link, null) as string;
        }

        private void Set(string name, string value)
        {
            linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, new object[] { value });
        }

        public string FullName => Get(nameof(FullName));

        public string TargetPath
        {
            get => Get(nameof(TargetPath));
            set => Set(nameof(TargetPath), value);
        }

        public string Arguments
        {
            get => Get(nameof(Arguments));
            set => Set(nameof(Arguments), value);
        }

        public string WorkingDirectory
        {
            get => Get(nameof(WorkingDirectory));
            set => Set(nameof(WorkingDirectory), value);
        }

        public string IconLocation
        {
            get => Get(nameof(IconLocation));
            set => Set(nameof(IconLocation), value);
        }

        public string Description
        {
            get => Get(nameof(Description));
            set => Set(nameof(Description), value);
        }

        public void Save()
        {
            linkType.InvokeMember(nameof(Save), BindingFlags.InvokeMethod, null, link, null);
        }
    }
}
