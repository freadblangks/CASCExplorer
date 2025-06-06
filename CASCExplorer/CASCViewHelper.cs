﻿using CASCExplorer.Properties;
using CASCExplorer.ViewPlugin;
using CASCLib;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CASCExplorer
{
    delegate void OnStorageChangedDelegate();
    delegate void OnCleanupDelegate();

    class CASCViewHelper
    {
        private ExtractProgress extractProgress;
        private CASCHandler _casc;
        private CASCFolder _root;
        private CASCFolder _currentFolder;
        private List<ICASCEntry> _displayedEntries;
        private CASCEntrySorter Sorter = new CASCEntrySorter();
        private ScanForm scanForm;
        private NumberFormatInfo sizeNumberFmt = new NumberFormatInfo()
        {
            NumberGroupSizes = new int[] { 3, 3, 3, 3, 3 },
            NumberDecimalDigits = 0,
            NumberGroupSeparator = " "
        };

        private AggregateCatalog m_catalog;

        [ImportMany(AllowRecomposition = true)]
        private List<Lazy<IPreview, IExtensions>> ViewPlugins { get; set; }

        private Control m_currentControl;

        public Panel ViewPanel { get; set; }

        public event OnStorageChangedDelegate OnStorageChanged;
        public event OnCleanupDelegate OnCleanup;

        public CASCHandler CASC => _casc;

        public CASCFolder Root => _root;

        public CASCFolder CurrentFolder => _currentFolder;

        public List<ICASCEntry> DisplayedEntries => _displayedEntries;

        internal CASCViewHelper()
        {
            ComposePlugins();
        }

        private void ComposePlugins()
        {
            m_catalog = new AggregateCatalog();
            m_catalog.Catalogs.Add(new AssemblyCatalog(Assembly.GetExecutingAssembly()));
            m_catalog.Catalogs.Add(new DirectoryCatalog(Application.StartupPath));

            try
            {
                var container = new CompositionContainer(m_catalog);
                container.ComposeParts(this);
            }
            catch (CompositionException ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public bool AnalyzeSoundFiles { get; set; } = true;
        public bool AddFileDataIdToSoundFiles { get; set; } = true;

        public void ExtractFiles(NoFlickerListView filesList)
        {
            if (_currentFolder == null)
                return;

            if (!filesList.HasSelection)
                return;

            if (extractProgress == null)
                extractProgress = new ExtractProgress();

            var files = CASCFolder.GetFiles(_displayedEntries, filesList.SelectedIndices.Cast<int>()).ToList();
            extractProgress.SetExtractData(_casc, files);
            extractProgress.ShowDialog();
        }

        public async Task ExtractInstallFiles(Action<int> progressCallback)
        {
            if (_casc == null)
                return;

            IProgress<int> progress = new Progress<int>(progressCallback);

            await Task.Run(() => {
                string[] platforms = ["Windows", "OSX"];

                foreach (string platform in platforms)
                {
                    var installFiles = _casc.Install.GetEntriesByTags(platform, "x86_64", "US");
                    var build = _casc.Config.BuildName;

                    int numFiles = installFiles.Count();
                    int numDone = 0;

                    foreach (var file in installFiles)
                    {
                        if (_casc.Encoding.GetEntry(file.MD5, out EncodingEntry enc))
                            _casc.SaveFileTo(enc.Keys[0], Path.Combine("data", build, $"{platform}_install_files"), file.Name);

                        progress.Report((int)(++numDone / (float)numFiles * 100));
                    }
                }
            });
        }

        public async Task AnalyzeUnknownFiles(Action<int> progressCallback)
        {
            if (_casc == null)
                return;

            IProgress<int> progress = new Progress<int>(progressCallback);

            await Task.Run(() =>
            {
                FileScanner scanner = new FileScanner(_casc, _root);

                if (_casc.Config.GameType == CASCGameType.WoW)
                {
                    Dictionary<int, List<string>> idToName = new Dictionary<int, List<string>>();

                    if (AnalyzeSoundFiles)
                    {
                        if (_casc.FileExists("DBFilesClient\\SoundEntries.db2"))
                        {
                            using (Stream stream = _casc.OpenFile("DBFilesClient\\SoundEntries.db2"))
                            {
                                WDB2Reader se = new WDB2Reader(stream);

                                foreach (var row in se)
                                {
                                    string name = row.Value.GetField<string>(2);

                                    int type = row.Value.GetField<int>(1);

                                    bool many = row.Value.GetField<int>(4) > 0;

                                    for (int i = 3; i < 23; i++)
                                    {
                                        int id = row.Value.GetField<int>(i);

                                        if (!idToName.ContainsKey(id))
                                            idToName[id] = new List<string>();

                                        idToName[id].Add("unknown\\sound\\" + name + (many ? "_" + (i - 2).ToString("D2") : "") + (type == 28 ? ".mp3" : ".ogg"));
                                    }
                                }
                            }
                        }

                        if (_casc.FileExists(1237434/*"DBFilesClient\\SoundKit.db2"*/) && _casc.FileExists(1237435/*"DBFilesClient\\SoundKitEntry.db2"*/) && _casc.FileExists(1665033/*"DBFilesClient\\SoundKitName.db2"*/))
                        {
                            using (Stream skStream = _casc.OpenFile(1237434))
                            using (Stream skeStream = _casc.OpenFile(1237435))
                            using (Stream sknStream = _casc.OpenFile(1665033))
                            {
                                Func<ulong, bool> keyCheckFunc = x => KeyService.GetKey(x) != null;
                                WDC3Reader sk = new WDC3Reader(skStream, keyCheckFunc);
                                WDC3Reader ske = new WDC3Reader(skeStream, keyCheckFunc);
                                WDC3Reader skn = new WDC3Reader(sknStream, keyCheckFunc);

                                Dictionary<int, List<int>> lookup = new Dictionary<int, List<int>>();

                                foreach (var row in ske)
                                {
                                    int soundKitId = row.Value.GetField<int>(0);

                                    if (!lookup.ContainsKey(soundKitId))
                                        lookup[soundKitId] = new List<int>();

                                    lookup[soundKitId].Add(row.Value.GetField<int>(1));
                                }

                                foreach (var row in sk)
                                {
                                    WDC3Row sknRow = skn.GetRow(row.Key);

                                    if (sknRow != null)
                                    {
                                        string name = sknRow.GetField<string>(0).Replace(':', '_').Replace("\"", "");

                                        int type = row.Value.GetField<byte>(6);

                                        if (!lookup.TryGetValue(row.Key, out List<int> ske_entries))
                                            continue;

                                        bool many = ske_entries.Count > 1;

                                        int i = 0;

                                        foreach (var fid in ske_entries)
                                        {
                                            if (!idToName.ContainsKey(fid))
                                                idToName[fid] = new List<string>();

                                            if (AddFileDataIdToSoundFiles)
                                                idToName[fid].Add("unknown\\sound\\" + name + (many ? "_" + (i + 1).ToString("D2") : "") + "_" + fid + (type == 28 ? ".mp3" : ".ogg"));
                                            else
                                                idToName[fid].Add("unknown\\sound\\" + name + (many ? "_" + (i + 1).ToString("D2") : "") + (type == 28 ? ".mp3" : ".ogg"));

                                            i++;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    CASCFolder unknownFolder = _root.GetFolder("unknown");

                    if (unknownFolder != null)
                    {
                        foreach (var kv in idToName)
                        {
                            foreach (var fn in kv.Value)
                                Logger.WriteLine($"{kv.Key};{fn}");
                        }

                        IEnumerable<CASCFile> files = CASCFolder.GetFiles(unknownFolder.Folders.Select(kv => kv.Value as ICASCEntry)
                            .Concat(unknownFolder.Files.Select(kv => kv.Value)), null, true)
                            .ToList();

                        int numTotal = files.Count();
                        int numDone = 0;

                        WowRootHandler wowRoot = _casc.Root as WowRootHandler;

                        if (wowRoot != null)
                        {
                            char[] PathDelimiters = new char[] { '/', '\\' };

                            foreach (var unknownEntry in files)
                            {
                                CASCFile unknownFile = unknownEntry;

                                if (idToName.TryGetValue(wowRoot.GetFileDataIdByHash(unknownFile.Hash), out List<string> name))
                                {
                                    if (name.Count == 1)
                                        unknownFile.FullName = name[0];
                                    else
                                    {
                                        unknownFolder.Files.Remove(unknownFile.Name);

                                        foreach (var file in name)
                                        {
                                            //Logger.WriteLine(file);

                                            string[] parts = file.Split(PathDelimiters);

                                            string entryName = parts[parts.Length - 1];

                                            ulong filehash = unknownFile.Hash;

                                            CASCFile entry = new CASCFile(filehash, file);
                                            CASCFile.Files[filehash] = entry;

                                            unknownFolder.Files[entryName] = entry;
                                        }
                                    }
                                }
                                else
                                {
                                    string ext = scanner.GetFileExtension(unknownFile);
                                    unknownFile.FullName += ext;

                                    if (ext == ".m2")
                                    {
                                        using (var m2file = _casc.OpenFile(unknownFile.Hash))
                                        using (var br = new BinaryReader(m2file))
                                        {
                                            m2file.Position = 0x14;
                                            int nameOffs = br.ReadInt32();

                                            string m2name;
                                            if (nameOffs == 0)
                                                m2name = wowRoot.GetFileDataIdByHash(unknownFile.Hash).ToString();
                                            else
                                            {
                                                m2file.Position = nameOffs + 8; // + sizeof(MD21)
                                                m2name = br.ReadCString();
                                            }
                                            unknownFile.FullName = "unknown\\" + m2name + ".m2";

                                            Logger.WriteLine($"{wowRoot.GetFileDataIdByHash(unknownFile.Hash)};{unknownFile.FullName}");
                                        }
                                    }
                                }

                                progress.Report((int)(++numDone / (float)numTotal * 100));
                            }
                        }
                    }
                }

                _casc.Root.Dump(_casc.Encoding);
            });
        }

        public void ScanFiles()
        {
            if (_casc == null || _root == null)
                return;

            if (scanForm == null)
            {
                scanForm = new ScanForm();
                scanForm.Initialize(_casc, _root);
            }

            scanForm.Reset();
            scanForm.Show();
        }

        public void UpdateListView(CASCFolder baseEntry, NoFlickerListView fileList, string filter)
        {
            Wildcard wildcard = new Wildcard(filter, false, RegexOptions.IgnoreCase);

            // Sort
            _displayedEntries = baseEntry.Folders.Select(kv => kv.Value as ICASCEntry).Concat(baseEntry.Files.Select(kv => kv.Value).Where(file => wildcard.IsMatch(file.Name))).
                OrderBy(v => v, Sorter).ToList();

            _currentFolder = baseEntry;

            // Update
            fileList.VirtualListSize = 0;
            fileList.VirtualListSize = _displayedEntries.Count;

            if (fileList.VirtualListSize > 0)
            {
                fileList.EnsureVisible(0);
                fileList.SelectedIndex = 0;
                fileList.FocusedItem = fileList.Items[0];
            }
        }

        public void CreateTreeNodes(TreeNode node)
        {
            CASCFolder baseEntry = node.Tag as CASCFolder;

            // check if we have dummy node
            if (node.Nodes["tempnode"] != null)
            {
                // remove dummy node
                node.Nodes.Clear();

                var orderedEntries = baseEntry.Folders.OrderBy(v => v.Value.Name);

                // Create nodes dynamically
                foreach (var it in orderedEntries)
                {
                    if (node.Nodes[it.Value.Name] == null)
                    {
                        TreeNode newNode = node.Nodes.Add(it.Value.Name);
                        newNode.Tag = it.Value;
                        newNode.Name = it.Value.Name;

                        if (it.Value.Folders.Count > 0)
                            newNode.Nodes.Add(new TreeNode() { Name = "tempnode" }); // add dummy node
                    }
                }
            }
        }

        public void OpenStorage(bool online, string path, string product)
        {
            Cleanup();

            using (var initForm = new InitForm())
            {
                initForm.LoadStorage((online, path, product));

                DialogResult res = initForm.ShowDialog();

                if (res != DialogResult.OK)
                    return;

                _casc = initForm.CASC;
                _root = initForm.Root;
            }

            Sorter.CASC = _casc;

            OnStorageChanged?.Invoke();
        }

        public void ChangeLocale(string locale)
        {
            if (_casc == null)
                return;

            OnCleanup?.Invoke();

            Settings.Default.LocaleFlags = (LocaleFlags)Enum.Parse(typeof(LocaleFlags), locale);

            _root = _casc.Root.SetFlags(Settings.Default.LocaleFlags, Settings.Default.OverrideArchive, Settings.Default.PreferHighResTextures);
            _casc.Root.MergeInstall(_casc.Install);

            OnStorageChanged?.Invoke();
        }

        public void SetOverrideArchive(bool overrideArchive, bool preferHighResTextures)
        {
            if (_casc == null)
                return;

            OnCleanup?.Invoke();

            Settings.Default.OverrideArchive = overrideArchive;
            Settings.Default.PreferHighResTextures = preferHighResTextures;

            _root = _casc.Root.SetFlags(Settings.Default.LocaleFlags, Settings.Default.OverrideArchive, Settings.Default.PreferHighResTextures);
            _casc.Root.MergeInstall(_casc.Install);

            OnStorageChanged?.Invoke();
        }

        public void SetSort(int column)
        {
            Sorter.SortColumn = column;
            Sorter.Order = Sorter.Order == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
        }

        public void GetSize(NoFlickerListView fileList)
        {
            if (_currentFolder == null)
                return;

            if (!fileList.HasSelection)
                return;

            var files = CASCFolder.GetFiles(_displayedEntries, fileList.SelectedIndices.Cast<int>());

            long size = files.Sum(f => (long)f.GetSize(_casc));

            MessageBox.Show(string.Format(sizeNumberFmt, "{0:N} bytes", size));
        }

        private void ExecPlugin(IPreview plugin, ICASCEntry file)
        {
            try
            {
                using (var stream = _casc.OpenFile(file.Hash))
                {
                    // todo: use Task
                    var control = plugin.Show(stream, file.Name);
                    if (m_currentControl != control)
                    {
                        ViewPanel.Controls.Clear();
                        ViewPanel.Controls.Add(control);
                        control.Dock = DockStyle.Fill;
                        m_currentControl = control;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Plugin Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void PreviewFile(NoFlickerListView fileList)
        {
            if (_currentFolder == null || ViewPanel == null || ViewPlugins == null)
                return;

            if (!fileList.HasSingleSelection)
                return;

            var file = _displayedEntries[fileList.SelectedIndex] as CASCFile;

            if (file == null)
            {
                ViewPanel?.Controls.Clear();
                m_currentControl = null;
                return;
            }

            var extension = Path.GetExtension(file.Name);

            foreach (var plugin in ViewPlugins)
            {
                if (plugin.Metadata.Extensions?.Contains(extension, StringComparer.InvariantCultureIgnoreCase) == true)
                {
                    ExecPlugin(plugin.Value, file);
                    return;
                }
            }

            var defPlugin = ViewPlugins.Where(p => p.Metadata.Extensions == null).FirstOrDefault();
            if (defPlugin != null)
            {
                ExecPlugin(defPlugin.Value, file);
                return;
            }

            m_currentControl = null;
            ViewPanel.Controls.Clear();
        }

        public void CreateListViewItem(RetrieveVirtualItemEventArgs e)
        {
            if (_currentFolder == null)
                return;

            if (e.ItemIndex < 0 || e.ItemIndex >= _displayedEntries.Count)
                return;

            ICASCEntry entry = _displayedEntries[e.ItemIndex];

            var localeFlags = LocaleFlags.None;
            var contentFlags = ContentFlags.None;
            var size = "<DIR>";

            if (entry is CASCFile)
            {
                size = _casc.GetFileSize(entry.Hash).ToString("N", sizeNumberFmt);

                var rootInfosLocale = _casc.Root.GetEntries(entry.Hash);

                if (rootInfosLocale.Any())
                {
                    foreach (var rootInfo in rootInfosLocale)
                    {
                        localeFlags |= rootInfo.LocaleFlags;
                        contentFlags |= rootInfo.ContentFlags;
                    }
                }
            }

            e.Item = new ListViewItem(new string[]
            {
                entry.Name,
                entry is CASCFolder ? "Folder" : Path.GetExtension(entry.Name),
                localeFlags.ToString(),
                contentFlags.ToString(),
                size
            })
            { ImageIndex = entry is CASCFolder ? 0 : 2 };
        }

        public void Cleanup()
        {
            Sorter.CASC = null;

            _currentFolder = null;
            _root = null;

            _displayedEntries?.Clear();
            _displayedEntries = null;

            _casc?.Clear();
            _casc = null;

            ViewPanel?.Controls.Clear();
            m_currentControl = null;

            OnCleanup?.Invoke();
        }

        public void Search(NoFlickerListView fileList, SearchForVirtualItemEventArgs e)
        {
            bool ignoreCase = true;
            bool searchUp = false;
            int SelectedIndex = fileList.SelectedIndex;

            var comparisonType = ignoreCase
                                    ? StringComparison.InvariantCultureIgnoreCase
                                    : StringComparison.InvariantCulture;

            if (searchUp)
            {
                for (var i = SelectedIndex - 1; i >= 0; --i)
                {
                    var op = _displayedEntries[i].Name;
                    if (op.IndexOf(e.Text, comparisonType) != -1)
                    {
                        e.Index = i;
                        break;
                    }
                }
            }
            else
            {
                for (int i = SelectedIndex + 1; i < fileList.Items.Count; ++i)
                {
                    var op = _displayedEntries[i].Name;
                    if (op.IndexOf(e.Text, comparisonType) != -1)
                    {
                        e.Index = i;
                        break;
                    }
                }
            }
        }

        public void ExportListFile()
        {
            WowRootHandler wowRoot = CASC.Root as WowRootHandler;

            using (StreamWriter sw = new StreamWriter(wowRoot != null ? "listfile_export.csv" : "listfile_export.txt"))
            {
                foreach (var file in CASCFile.Files.OrderBy(f => f.Value.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    if (CASC.FileExists(file.Key) && (wowRoot == null || !wowRoot.IsUnknownFile(file.Key)))
                    {
                        if (wowRoot != null)
                        {
                            int fileDataId = wowRoot.GetFileDataIdByHash(file.Key);
                            sw.WriteLine($"{fileDataId};{file.Value.FullName}");
                        }
                        else
                        {
                            sw.WriteLine(file.Value.FullName);
                        }
                    }
                }
            }
        }

        public void ExportFolders()
        {
            WowRootHandler wowRoot = CASC.Root as WowRootHandler;

            using (StreamWriter sw = new StreamWriter("dirs.txt"))
            {
                HashSet<string> dirData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in CASCFile.Files.OrderBy(f => f.Value.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    if (CASC.FileExists(file.Key) && (wowRoot == null || !wowRoot.IsUnknownFile(file.Key)))
                    {
                        ulong fileHash = file.Key;

                        int dirSepIndex = file.Value.FullName.LastIndexOf('\\');

                        if (dirSepIndex >= 0)
                        {
                            string dir = file.Value.FullName.Substring(0, dirSepIndex);

                            dirData.Add(dir);
                        }
                    }
                }

                foreach (var dir in dirData)
                {
                    sw.WriteLine(dir);
                }

                Logger.WriteLine("WowRootHandler: loaded {0} valid file names", CASCFile.Files.Count);
            }
        }

        public void ExtractCASCSystemFiles()
        {
            if (_casc == null)
                return;

            _casc.SaveFileTo(_casc.Config.EncodingEKey, ".", "encoding");

            //_casc.SaveFileTo(_casc.Config.PatchKey, ".", "patch");

            if (_casc.Encoding.GetEntry(_casc.Config.RootCKey, out EncodingEntry enc))
                _casc.SaveFileTo(enc.Keys[0], ".", "root");

            if (_casc.Encoding.GetEntry(_casc.Config.InstallCKey, out enc))
                _casc.SaveFileTo(enc.Keys[0], ".", "install");

            if (_casc.Encoding.GetEntry(_casc.Config.DownloadCKey, out enc))
                _casc.SaveFileTo(enc.Keys[0], ".", "download");

            //if (_casc.Encoding.GetEntry(_casc.Config.PartialPriorityMD5, out enc))
            //    _casc.SaveFileTo(enc.Key, ".", "partial-priority");
        }
    }
}
