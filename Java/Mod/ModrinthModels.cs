using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public enum ModProjectType
    {
        Mod = 0,
        Modpack = 1,
        DataPack = 2,
        ResourcePack = 3,
        Shader = 4
    }

    public class ModrinthMod : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string IconUrl { get; set; }
        public int Downloads { get; set; }
        public string Author { get; set; }
        public string ProjectType { get; set; }

        private ImageSource _icon;
        public ImageSource Icon
        {
            get { return _icon; }
            set { _icon = value; Raise("Icon"); }
        }

        public string DownloadsText
        {
            get { return "⬇ " + Downloads.ToString("N0") + " 下载"; }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string p)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(p));
        }
    }

    public class ModVersion
    {
        public string Id { get; set; }
        public string ProjectId { get; set; }
        public string VersionNumber { get; set; }
        public string Changelog { get; set; }
        public DateTime DatePublished { get; set; }
        public int Downloads { get; set; }
        public string VersionType { get; set; }
        public bool Featured { get; set; }
        public List<string> GameVersions { get; set; }
        public List<string> Loaders { get; set; }
        public List<ModFile> Files { get; set; }
        public string Status { get; set; }
        public string RequestedStatus { get; set; }

        public string GameVersionsText
        {
            get { return GameVersions == null ? "" : string.Join(", ", GameVersions.ToArray()); }
        }
        public string LoadersText
        {
            get { return Loaders == null ? "" : string.Join(", ", Loaders.ToArray()); }
        }
        public string DateText
        {
            get { return DatePublished.ToString("yyyy-MM-dd"); }
        }
        public string DownloadsText
        {
            get { return Downloads.ToString("N0"); }
        }
    }

    public class ModFile
    {
        public string Url { get; set; }
        public string Filename { get; set; }
        public bool Primary { get; set; }
        public long Size { get; set; }
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public List<string> GameVersions { get; set; }
        public List<string> Loaders { get; set; }
    }
}