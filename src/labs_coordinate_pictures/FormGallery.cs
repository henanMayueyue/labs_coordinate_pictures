﻿// Copyright (c) Ben Fisher, 2016.
// Licensed under GPLv3. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace labs_coordinate_pictures
{
    public partial class FormGallery : Form
    {
        // see SortingImages.md for a description of modes and categories.
        // the 'mode' specifies filetypes, and can add custom commands
        ModeBase _mode;

        // is this a large image that needed to be resized
        bool _currentImageResized = false;

        // if user control-clicks on a large image, we'll show a zoomed portion of image
        bool _zoomed = false;

        // during long-running operations on bg threads, we block most UI input
        bool _enabled = true;

        // user can add custom categories; we store the original menu in order to restore later
        List<ToolStripItem> _originalCategoriesMenu;
        List<ToolStripItem> _originalEditMenu;

        // shortcut key bindings from letter, to category.
        Dictionary<string, string> _categoryKeyBindings;

        // support undoing file moves
        UndoStack<Tuple<string, string>> _undoFileMoves =
            new UndoStack<Tuple<string, string>>();

        // placeholder image
        Bitmap _bitmapBlank = new Bitmap(1, 1);

        // smart directory-list object that updates itself if filenames are changed
        FileListNavigation _filelist;

        // cache of images; we'll prefetch images into the cache on a bg thread
        ImageCache _imagecache;

        // how many images to store in cache
        const int ImageCacheSize = 16;

        // how many images to prefetch after the user moves to the next image
        const int ImageCacheBatch = 8;

        public FormGallery(ModeBase mode, string initialDirectory, string initialFilepath = "")
        {
            InitializeComponent();

            SimpleLog.Current.WriteLog("Starting session in " + initialDirectory + "|" + initialFilepath);
            _mode = mode;
            _originalCategoriesMenu = new List<ToolStripItem>(
                categoriesToolStripMenuItem.DropDownItems.Cast<ToolStripItem>());
            _originalEditMenu = new List<ToolStripItem>(
                editToolStripMenuItem.DropDownItems.Cast<ToolStripItem>());

            // event handlers
            movePrevMenuItem.Click += (sender, e) => MoveOne(false);
            moveNextMenuItem.Click += (sender, e) => MoveOne(true);
            moveManyPrevToolStripMenuItem.Click += (sender, e) => MoveMany(false);
            moveManyNextToolStripMenuItem.Click += (sender, e) => MoveMany(true);
            moveFirstToolStripMenuItem.Click += (sender, e) => MoveFirst(true);
            moveLastToolStripMenuItem.Click += (sender, e) => MoveFirst(false);
            moveToTrashToolStripMenuItem.Click += (sender, e) => KeyDelete();
            renameItemToolStripMenuItem.Click += (sender, e) => RenameFile();
            undoMoveToolStripMenuItem.Click += (sender, e) => UndoOrRedo(true);
            redoMoveToolStripMenuItem.Click += (sender, e) => UndoOrRedo(false);

            _filelist = new FileListNavigation(
                initialDirectory, _mode.GetFileTypes(), true, true, initialFilepath);

            pictureBox.SizeMode = PictureBoxSizeMode.Normal;
            ModeUtils.UseDefaultCategoriesIfFirstRun(mode);
            RefreshCategories();
            OnOpenItem();
        }

        public FileListNavigation GetFilelist()
        {
            return _filelist;
        }

        public ImageCache GetImageCache()
        {
            return _imagecache;
        }

        void OnOpenItem()
        {
            // if the user resized the window, create a new cache for the new size
            pictureBox.Image = _bitmapBlank;
            if (_imagecache == null || _imagecache.MaxWidth != pictureBox.Width ||
                _imagecache.MaxHeight != pictureBox.Height)
            {
                RefreshImageCache();
            }

            if (_filelist.Current == null)
            {
                label.Text = "looks done.";
                pictureBox.Image = _bitmapBlank;
            }
            else
            {
                // tell the mode we've opened something
                _mode.OnOpenItem(_filelist.Current, this);

                // show the current image
                int originalWidth = 0, originalHeight = 0;
                pictureBox.Image = _imagecache.Get(_filelist.Current, out originalWidth, out originalHeight);
                _currentImageResized = originalWidth > _imagecache.MaxWidth ||
                    originalHeight > _imagecache.MaxHeight;

                var showResized = _currentImageResized ? "s" : "";
                label.Text = string.Format("{0} {1}\r\n{2} {3}({4}x{5})", _filelist.Current,
                    Utils.FormatFilesize(_filelist.Current), Path.GetFileName(_filelist.Current),
                    showResized, originalWidth, originalHeight);
            }

            renameToolStripMenuItem.Visible = _mode.SupportsRename();
            _zoomed = false;
        }

        void RefreshImageCache()
        {
            if (_imagecache != null)
            {
                pictureBox.Image = null;
                _imagecache.Dispose();
            }

            // provide callbacks for ImageCache to see if it can dispose an image.
            Func<Bitmap, bool> canDisposeBitmap =
                (bmp) => (bmp as object) != (pictureBox.Image as object);

            Func<Action, bool> callbackOnUiThread =
                (act) =>
                {
                    this.Invoke((MethodInvoker)(() => act.Invoke()));
                    return true;
                };

            _imagecache = new ImageCache(pictureBox.Width, pictureBox.Height,
                ImageCacheSize, callbackOnUiThread, canDisposeBitmap);
        }

        void RefreshFilelist()
        {
            _filelist.Refresh();
            MoveFirst();
        }

        void MoveOne(bool isNext)
        {
            // make a list with all items null
            var pathsToPrefetch = Enumerable.Repeat<string>(null, ImageCacheBatch).ToList();

            // move forward
            _filelist.GoNextOrPrev(isNext, pathsToPrefetch, pathsToPrefetch.Count);
            OnOpenItem();

            // asynchronously prefetch files that are likely to be seen next
            _imagecache.AddAsync(pathsToPrefetch);
        }

        void MoveMany(bool isNext)
        {
            for (int i = 0; i < 15; i++)
            {
                _filelist.GoNextOrPrev(isNext);
            }

            OnOpenItem();
        }

        void MoveFirst(bool isFirstOrLast = true)
        {
            if (isFirstOrLast)
            {
                _filelist.GoFirst();
            }
            else
            {
                _filelist.GoLast();
            }

            OnOpenItem();
        }

        void RefreshCustomCommands()
        {
            labelView.Text = "\r\n\r\n";
            if (_mode.GetDisplayCustomCommands().Length > 0)
            {
                // restore original Edit menu
                editToolStripMenuItem.DropDownItems.Clear();
                foreach (var item in _originalEditMenu)
                {
                    editToolStripMenuItem.DropDownItems.Add(item);
                }

                // add items to the Edit menu and to labelView.Text.
                editToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
                foreach (var tuple in _mode.GetDisplayCustomCommands())
                {
                    var menuItem = new ToolStripMenuItem(tuple.Item2);
                    menuItem.ShortcutKeyDisplayString = tuple.Item1;
                    menuItem.Click += (sender, e) => Utils.MessageBox(
                        "Press the shortcut " + tuple.Item1 + " to run this command.");

                    editToolStripMenuItem.DropDownItems.Add(menuItem);
                    labelView.Text += tuple.Item1 + "=" + tuple.Item2 + "\r\n\r\n";
                }
            }
        }

        void RefreshCategories()
        {
            RefreshCustomCommands();

            // restore original Categories menu
            categoriesToolStripMenuItem.DropDownItems.Clear();
            foreach (var item in _originalCategoriesMenu)
            {
                categoriesToolStripMenuItem.DropDownItems.Add(item);
            }

            // add items to the Categories menu and to labelView.Text.
            categoriesToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _categoryKeyBindings = new Dictionary<string, string>();
            var tuples = ModeUtils.ModeToTuples(_mode);
            foreach (var tuple in tuples)
            {
                var menuItem = new ToolStripMenuItem(tuple.Item2);
                menuItem.ShortcutKeyDisplayString = tuple.Item1;
                menuItem.Click += (sender, e) => AssignCategory(tuple.Item3);
                categoriesToolStripMenuItem.DropDownItems.Add(menuItem);
                labelView.Text += tuple.Item1 + "    " + tuple.Item2 + "\r\n\r\n";
                _categoryKeyBindings[tuple.Item1] = tuple.Item3;
            }

            // only show categories in the UI if enabled.
            if (!Configs.Current.GetBool(ConfigKey.GalleryViewCategories))
            {
                labelView.Text = "";
            }
        }

        private void viewCategoriesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var current = Configs.Current.GetBool(ConfigKey.GalleryViewCategories);
            Configs.Current.SetBool(ConfigKey.GalleryViewCategories, !current);
            RefreshCategories();
        }

        private void editCategoriesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // in the input box, suggest category strings.
            var suggestions = new string[]
            {
                Configs.Current.Get(_mode.GetCategories()),
                _mode.GetDefaultCategories()
            };

            var text = "Please enter a list of category strings separated by |. " +
                "Each category string must be in the form A/categoryReadable/categoryId, " +
                "where A is a single capital letter, categoryReadable will be the human-readable " +
                "label, and categoryID will be the unique ID (when an image is given this ID, the " +
                "ID will be added to the filename as a suffix).";
            var nextCategories = InputBoxForm.GetStrInput(
                text, null, InputBoxHistory.EditCategoriesString, suggestions);

            if (!string.IsNullOrEmpty(nextCategories))
            {
                try
                {
                    ModeUtils.CategoriesStringToTuple(nextCategories);
                }
                catch (CoordinatePicturesException exception)
                {
                    Utils.MessageErr(exception.Message);
                    return;
                }

                Configs.Current.Set(_mode.GetCategories(), nextCategories);
                RefreshCategories();
            }
        }

        public bool WrapMoveFile(string path, string pathDestination, bool addToUndo = true)
        {
            const int millisecondsRetryMoving = 3000;
            if (File.Exists(pathDestination))
            {
                Utils.MessageErr("already exists: " + pathDestination);
                return false;
            }

            if (!File.Exists(path))
            {
                Utils.MessageErr("does not exist: " + path);
                return false;
            }

            SimpleLog.Current.WriteLog("Moving (" + path + ") to (" + pathDestination + ")");
            try
            {
                bool succeeded = Utils.RepeatWhileFileLocked(path, millisecondsRetryMoving);
                if (!succeeded)
                {
                    SimpleLog.Current.WriteLog("Move failed, access denied.");
                    Utils.MessageErr("File is locked: " + path);
                    return false;
                }

                File.Move(path, pathDestination);
            }
            catch (IOException e)
            {
                Utils.MessageErr("IOException: " + e);
                return false;
            }

            if (addToUndo)
            {
                _undoFileMoves.Add(Tuple.Create(path, pathDestination));
            }

            return true;
        }

        public void UndoOrRedo(bool isUndo)
        {
            var moveConsidered = isUndo ?
                _undoFileMoves.PeekUndo() :
                _undoFileMoves.PeekRedo();

            if (moveConsidered == null)
            {
                Utils.MessageErr("nothing to undo", true);
            }
            else
            {
                var pathDestination = isUndo ? moveConsidered.Item1 : moveConsidered.Item2;
                var pathSource = isUndo ? moveConsidered.Item2 : moveConsidered.Item1;
                if (Configs.Current.SuppressDialogs ||
                    Utils.AskToConfirm("move " + pathSource + " back to " + pathDestination + "?"))
                {
                    // change the undo state only if the move succeeds.
                    if (WrapMoveFile(pathSource, pathDestination, addToUndo: false))
                    {
                        if (isUndo)
                        {
                            _undoFileMoves.Undo();
                        }
                        else
                        {
                            _undoFileMoves.Redo();
                        }
                    }
                }
            }
        }

        public void FormGallery_KeyUp(object sender, KeyEventArgs e)
        {
            // ToolStripMenuItem does have automatic keybinding by setting ShortcutKeys,
            // but it often requires Ctrl or Alt in the shortcutkey,
            // and it uses KeyDown, firing many times if key is held.
            // so we manually handle KeyUp.

            if (!_enabled)
            {
                return;
            }

            if (!e.Shift && !e.Control && !e.Alt)
            {
                if (e.KeyCode == Keys.F5)
                    RefreshFilelist();
                else if (e.KeyCode == Keys.Left)
                    MoveOne(false);
                else if (e.KeyCode == Keys.Right)
                    MoveOne(true);
                else if (e.KeyCode == Keys.PageUp)
                    MoveMany(false);
                else if (e.KeyCode == Keys.PageDown)
                    MoveMany(true);
                else if (e.KeyCode == Keys.Home)
                    MoveFirst(true);
                else if (e.KeyCode == Keys.End)
                    MoveFirst(false);
                else if (e.KeyCode == Keys.Delete)
                    KeyDelete();
                else if (e.KeyCode == Keys.H)
                    RenameFile();
            }
            else if (e.Shift && !e.Control && !e.Alt)
            {
                var categoryId = CheckKeyBindingsToAssignCategory(e.KeyCode, _categoryKeyBindings);
                if (categoryId != null)
                    AssignCategory(categoryId);
            }
            else if (e.Shift && e.Control && !e.Alt)
            {
                if (e.KeyCode == Keys.E)
                    editInAltEditorToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.X)
                    cropRotateFileToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.H)
                    replaceInFilenameToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.D3)
                    removeNumberedPrefixToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.V)
                    viewCategoriesToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.K)
                    editCategoriesToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.OemOpenBrackets)
                    convertToSeveralJpgsInDifferentQualitiesToolStripMenuItem_Click(null, null);
            }
            else if (!e.Shift && e.Control && !e.Alt)
            {
                if (e.KeyCode == Keys.W)
                    showInExplorerToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.C)
                    copyPathToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.E)
                    editFileToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.D3)
                    addNumberedPrefixToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.Z)
                    UndoOrRedo(true);
                else if (e.KeyCode == Keys.Y)
                    UndoOrRedo(false);
                else if (e.KeyCode == Keys.OemOpenBrackets)
                    convertResizeImageToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.D1)
                    saveSpacePngToWebpToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.D4)
                    saveSpaceOptimizeJpgToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.OemCloseBrackets)
                    keepAndDeleteOthersToolStripMenuItem_Click(null, null);
                else if (e.KeyCode == Keys.Enter)
                    finishedCategorizingToolStripMenuItem_Click(null, null);
            }

            _mode.OnCustomCommand(this, e.Shift, e.Alt, e.Control, e.KeyCode);
        }

        public static string CheckKeyBindingsToAssignCategory(Keys keysEnum,
            Dictionary<string, string> bindings)
        {
            // see if this is key is bound to a category.
            // number keys like 1 are represented as "D1".
            var key = keysEnum.ToString();
            if (key.Length == 1 ||
                (key.Length == 2 && key[0] == 'D' && Utils.IsDigits(key[1].ToString())))
            {
                key = key.Substring(key.Length - 1);
                if (bindings.ContainsKey(key))
                {
                    return bindings[key];
                }
            }

            return null;
        }

        // add a file to a category (appends to the filename, and it won't show in formgallery)
        void AssignCategory(string categoryId)
        {
            _mode.OnBeforeAssignCategory();
            if (_filelist.Current == null)
            {
                return;
            }

            var newname = FilenameUtils.AddCategoryToFilename(_filelist.Current, categoryId);
            if (WrapMoveFile(_filelist.Current, newname))
            {
                MoveOne(true);
            }
        }

        public void RenameFile()
        {
            if (_filelist.Current == null || !_mode.SupportsRename())
            {
                return;
            }

            InputBoxHistory mruList = FilenameUtils.LooksLikeImage(_filelist.Current) ?
                InputBoxHistory.RenameImage : (FilenameUtils.IsExt(_filelist.Current, ".wav") ?
                InputBoxHistory.RenameWavAudio : InputBoxHistory.RenameOther);

            // for convenience, don't include the numbered prefix or file extension.
            var current = FilenameUtils.GetFileNameWithoutNumberedPrefix(_filelist.Current);
            var currentNoExt = Path.GetFileNameWithoutExtension(current);

            var newName = InputBoxForm.GetStrInput("Enter a new name:", currentNoExt, mruList);
            if (!string.IsNullOrEmpty(newName))
            {
                var nameWithPrefix = Path.GetFileName(_filelist.Current);
                var hasNumberedPrefix = current != nameWithPrefix;
                var prefix = hasNumberedPrefix ?
                    nameWithPrefix.Substring(0, FilenameUtils.NumberedPrefixLength()) :
                    "";
                var fullnewname = Path.GetDirectoryName(_filelist.Current) + "\\" +
                    prefix + newName + Path.GetExtension(_filelist.Current);

                if (WrapMoveFile(_filelist.Current, fullnewname))
                {
                    RefreshUiAfterCurrentImagePossiblyMoved(fullnewname);
                }
            }
        }

        void RefreshUiAfterCurrentImagePossiblyMoved(string setCurrentPath)
        {
            _filelist.NotifyFileChanges();
            _filelist.TrySetPath(setCurrentPath);
            OnOpenItem();
        }

        void KeyDelete()
        {
            if (_filelist.Current != null)
            {
                _mode.OnBeforeAssignCategory();
                if (UndoableSoftDelete(_filelist.Current))
                {
                    MoveOne(true);
                }
            }
        }

        public bool UndoableSoftDelete(string path)
        {
            var pathDestination = Utils.GetSoftDeleteDestination(path);
            return WrapMoveFile(path, pathDestination);
        }

        // during long-running operations on bg threads, we block most UI input
        public void UIEnable(bool enabled)
        {
            _enabled = enabled;
            this.label.ForeColor = _enabled ? Color.Black : Color.Gray;
            fileToolStripMenuItem.Enabled = _enabled;
            editToolStripMenuItem.Enabled = _enabled;
            renameToolStripMenuItem.Enabled = _enabled;
            categoriesToolStripMenuItem.Enabled = _enabled;
        }

        private void showInExplorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_filelist.Current == null)
            {
                Utils.OpenDirInExplorer(_filelist.BaseDirectory);
            }
            else
            {
                Utils.SelectFileInExplorer(_filelist.Current);
            }
        }

        private void copyPathToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(_filelist.Current ?? "");
        }

        static void LaunchEditor(string exe, string path)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Utils.MessageErr("Could not find the application '" + exe +
                    "'. The location can be set in the Options menu.");
            }
            else
            {
                Utils.Run(exe, new string[] { path }, shellExecute: false, waitForExit: false, hideWindow: false);
            }
        }

        private void editFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (FilenameUtils.IsExt(_filelist.Current, ".webp"))
            {
                Process.Start(_filelist.Current);
            }
            else if (FilenameUtils.LooksLikeAudio(_filelist.Current))
            {
                LaunchEditor(Configs.Current.Get(ConfigKey.FilepathMediaEditor), _filelist.Current);
            }
            else
            {
                LaunchEditor(@"C:\Windows\System32\mspaint.exe", _filelist.Current);
            }
        }

        private void editInAltEditorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (FilenameUtils.IsExt(_filelist.Current, ".webp"))
            {
                LaunchEditor(@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe", _filelist.Current);
            }
            else if (FilenameUtils.LooksLikeAudio(_filelist.Current))
            {
                LaunchEditor(Configs.Current.Get(ConfigKey.FilepathMediaEditor), _filelist.Current);
            }
            else
            {
                LaunchEditor(Configs.Current.Get(ConfigKey.FilepathAltEditorImage), _filelist.Current);
            }
        }

        private void cropRotateFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (FilenameUtils.IsExt(_filelist.Current, ".jpg"))
            {
                LaunchEditor(Configs.Current.Get(ConfigKey.FilepathJpegCrop), _filelist.Current);
            }
            else if (FilenameUtils.LooksLikeAudio(_filelist.Current))
            {
                LaunchEditor(Configs.Current.Get(ConfigKey.FilepathMp3DirectCut), _filelist.Current);
            }
            else
            {
                LaunchEditor(@"C:\Windows\System32\mspaint.exe", _filelist.Current);
            }
        }

        // add a prefix to files, useful when renaming and you want to maintain the order
        private void addNumberedPrefixToolStripMenuItem_Click(object sender, EventArgs e)
        {
            int nAddedPrefix = 0, nSkippedPrefix = 0, nFailedToRename = 0;
            if (_mode.SupportsRename() && Utils.AskToConfirm("Add numbered prefix?"))
            {
                int number = 0;
                foreach (var path in _filelist.GetList())
                {
                    number++;
                    if (Path.GetFileName(path) == FilenameUtils.GetFileNameWithoutNumberedPrefix(path))
                    {
                        if (WrapMoveFile(path, FilenameUtils.AddNumberedPrefix(path, number)))
                        {
                            nAddedPrefix++;
                        }
                        else
                        {
                            nFailedToRename++;
                        }
                    }
                    else
                    {
                        nSkippedPrefix++;
                    }
                }

                MoveFirst();
            }

            Utils.MessageBox(string.Format("{0} files skipped because they already have a prefix, " +
                "{1} files failed to be renamed, {2} files successfully renamed.",
                nSkippedPrefix, nFailedToRename, nAddedPrefix));
        }

        private void removeNumberedPrefixToolStripMenuItem_Click(object sender, EventArgs e)
        {
            int nRemovedPrefix = 0, nSkippedAlready = 0, nFailedToRename = 0;
            if (_mode.SupportsRename() && Utils.AskToConfirm("Remove numbered prefix?"))
            {
                foreach (var path in _filelist.GetList())
                {
                    if (Path.GetFileName(path) == FilenameUtils.GetFileNameWithoutNumberedPrefix(path))
                    {
                        nSkippedAlready++;
                    }
                    else
                    {
                        if (WrapMoveFile(path, Path.GetDirectoryName(path) + "\\" +
                            FilenameUtils.GetFileNameWithoutNumberedPrefix(path)))
                        {
                            nRemovedPrefix++;
                        }
                        else
                        {
                            nFailedToRename++;
                        }
                    }
                }

                MoveFirst();
            }

            Utils.MessageBox(string.Format("{0} files skipped because they have no prefix, " +
                "{1} files failed to be renamed, {2} files successfully renamed.",
                nSkippedAlready, nFailedToRename, nRemovedPrefix));
        }

        // replace one string with another within a filename.
        private void replaceInFilenameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_filelist.Current == null || !_mode.SupportsRename())
            {
                return;
            }

            var filename = Path.GetFileName(_filelist.Current);
            var search = InputBoxForm.GetStrInput("Search for this in filename (not directory name):",
                filename, InputBoxHistory.RenameReplaceInName);
            if (!string.IsNullOrEmpty(search))
            {
                var replace = InputBoxForm.GetStrInput(
                    "Replace with this:", filename, InputBoxHistory.RenameReplaceInName);
                if (replace != null && filename.Contains(search))
                {
                    var pathDestination = Path.GetDirectoryName(_filelist.Current) + "\\" +
                        filename.Replace(search, replace);
                    if (WrapMoveFile(_filelist.Current, pathDestination))
                    {
                        RefreshUiAfterCurrentImagePossiblyMoved(pathDestination);
                    }
                }
            }
        }

        // ctrl-clicking a large image will show a zoomed-in view.
        private void pictureBox1_MouseUp(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                if (_zoomed)
                {
                    OnOpenItem();
                }
                else if (_currentImageResized)
                {
                    _imagecache.Excerpt.MakeBmp(_filelist.Current, e.X, e.Y,
                        pictureBox.Image.Width, pictureBox.Image.Height);
                    pictureBox.Image = _imagecache.Excerpt.Bmp;
                    _zoomed = true;
                }
            }
        }

        // calls a Python script to convert or resize an image.
        private void convertResizeImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_filelist.Current == null || !FilenameUtils.LooksLikeImage(_filelist.Current))
            {
                return;
            }

            var suggestions = new string[] { "50%", "100%", "70%" };
            var resize = InputBoxForm.GetStrInput("Resize by what value (example 50%):",
                null, more: suggestions, useClipboard: false);
            if (string.IsNullOrEmpty(resize))
            {
                return;
            }

            var checkResizeSpec = new Regex(@"^[0-9]+[h%]$");
            if (!checkResizeSpec.IsMatch(resize))
            {
                Utils.MessageErr("invalid resize spec.");
                return;
            }

            suggestions = new string[] { "png|100", "jpg|90", "webp|100" };
            var fmt = InputBoxForm.GetStrInput("Convert to format|quality:",
                null, InputBoxHistory.EditConvertResizeImage, suggestions, useClipboard: false);
            if (string.IsNullOrEmpty(fmt))
            {
                return;
            }

            var checkFormat = new Regex(@"^[a-zA-Z]+\|[1-9][0-9]*$");
            if (!checkFormat.IsMatch(fmt))
            {
                Utils.MessageErr("invalid format string.");
                return;
            }

            var parts = fmt.Split(new char[] { '|' });
            int nQual = int.Parse(parts[1]);
            var outFile = Path.GetDirectoryName(_filelist.Current) + "\\" +
                Path.GetFileNameWithoutExtension(_filelist.Current) + "_out." + parts[0];
            Utils.RunImageConversion(_filelist.Current, outFile, resize, nQual);
        }

        private void RunBatchOptimize(IEnumerable<string> files, bool isJpeg, bool stripAllExif, 
            string suffix, int minSavings = 0, int pauseEvery = 150)
        {
            int countOptimized = 0, countNotOptimized = 0;
            long saved = 0;
            foreach (var path in files)
            {
                var pathOut = Path.GetDirectoryName(path) + "\\" +
                    Path.GetFileNameWithoutExtension(path) + suffix;

                if (pauseEvery > 0 && countOptimized % pauseEvery == pauseEvery - 1)
                {
                    MessageBox.Show("Pausing... Click OK to continue");
                }

                if (!File.Exists(pathOut))
                {
                    try
                    {
                        // run conversion, then delete the larger of the resulting pair of images.
                        if (isJpeg)
                        {
                            Utils.JpgLosslessOptimize(path, pathOut, stripAllExif);

                            if (!stripAllExif)
                            {
                                Utils.JpgStripThumbnails(pathOut);
                            }
                        }
                        else
                        {
                            Utils.RunImageConversion(path, pathOut, "100%", 100);
                        }

                        const int minOutputSize = 16;
                        var oldLength = new FileInfo(path).Length;
                        var newLength = new FileInfo(pathOut).Length;
                        if (oldLength - newLength > minSavings && newLength >= minOutputSize)
                        {
                            countOptimized++;
                            saved += oldLength - newLength;
                            Utils.SoftDelete(path);
                            SimpleLog.Current.WriteLog(
                                    "optimizing " + path + " to " + pathOut + ": keeping new");

                            if (isJpeg)
                            {
                                // move from _optimmed.jpg to .jpg
                                File.Move(pathOut, path);
                            }
                        }
                        else
                        {
                            if (newLength < minOutputSize)
                            {
                                SimpleLog.Current.WriteError(
                                    "optimizing " + path + " to " + pathOut + ": result too small");
                            }
                            else
                            {
                                SimpleLog.Current.WriteLog(
                                    "optimizing " + path + " to " + pathOut + ": keeping original");
                            }

                            countNotOptimized++;
                            File.Delete(pathOut);
                        }
                    }
                    catch (Exception exc)
                    {
                        Utils.MessageErr("Exception when converting " +
                            path + ": " + exc);
                    }
                }
            }

            Utils.MessageBox("Complete. " +
                countOptimized + "file(s) optimized, " + countNotOptimized + " file(s) were better as originals.\n\n" +
                string.Format(" Saved {0:0.00}mb.", saved / (1024.0 * 1024.0)));

            this.Invoke(new Action(() =>
            {
                RefreshUiAfterCurrentImagePossiblyMoved(_filelist.Current);
            }));
        }

        private void saveSpaceOptimizeJpgToolStripMenuItem_Click(object sender, EventArgs e)
        {
            const int minSavings = 30 * 1024;
            int minSize = 0;
            var strMinSize = InputBoxForm.GetStrInput("Only optimize jpg files with size greater than this many kb:",
                "100", useClipboard: false);
            if (int.TryParse(strMinSize, out minSize))
            {
                var stripAllExif = false;
                var list = _filelist.GetList().Where(
                    (item) => item.ToLowerInvariant().EndsWith(".jpg") &&
                    new FileInfo(item).Length > 1024 * minSize);

                RunLongActionInThread(new Action(() =>
                {
                    RunBatchOptimize(list, true, stripAllExif, "_optimmed.jpg", minSavings);
                }));
            }
        }

        private void saveSpacePngToWebpToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // webp converter can be slow for large images, so ask the user first.
            var listIncludingLarge = _filelist.GetList().Where(
                (item) => item.ToLowerInvariant().EndsWith(".png"));
            var list = new List<string>();
            foreach (var path in listIncludingLarge)
            {
                if (new FileInfo(path).Length < 1024 * 500 ||
                    Utils.AskToConfirm("include the large file " +
                        Path.GetFileName(path) + "\r\n" + Utils.FormatFilesize(path) + "?"))
                {
                    list.Add(path);
                }
            }

            RunLongActionInThread(new Action(() =>
            {
                RunBatchOptimize(list, false, false, ".webp");
            }));
        }

        // makes several images at different jpg qualities.
        private void convertToSeveralJpgsInDifferentQualitiesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_filelist.Current == null || !FilenameUtils.LooksLikeImage(_filelist.Current))
            {
                return;
            }

            RunLongActionInThread(new Action(() =>
            {
                var qualities = new int[] { 96, 94, 92, 90, 85, 80, 75, 70, 60 };
                foreach (var quality in qualities)
                {
                    var pathOutput = _filelist.Current + quality.ToString() + ".jpg";
                    Utils.RunImageConversion(_filelist.Current, pathOutput, "100%", quality);
                }
            }));
        }

        // after making several images at different jpg qualities, keep the current image and remove the rest.
        private void keepAndDeleteOthersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_filelist.Current == null || !FilenameUtils.LooksLikeImage(_filelist.Current))
            {
                return;
            }

            bool nameHasSuffix;
            string pathWithoutSuffix;
            var pathsToDelete = FindSimilarFilenames.FindSimilarNames(
                _filelist.Current, _mode.GetFileTypes(), _filelist.GetList(),
                out nameHasSuffix, out pathWithoutSuffix);

            if (Utils.AskToConfirm("Delete the extra files \r\n" +
                string.Join("\r\n", pathsToDelete) + "\r\n?"))
            {
                foreach (var path in pathsToDelete)
                {
                    UndoableSoftDelete(path);
                }

                // rename this file from a.png60.jpg to a.jpg
                if (nameHasSuffix && WrapMoveFile(_filelist.Current, pathWithoutSuffix))
                {
                    RefreshUiAfterCurrentImagePossiblyMoved(pathWithoutSuffix);
                }
            }
        }

        // modes provide a 'completion action' that is called for each file assigned to a category.
        private void finishedCategorizingToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!_mode.SupportsCompletionAction())
            {
                Utils.MessageBox("Mode does not have an associated action.");
                return;
            }

            if (Utils.AskToConfirm("Apply finishing?"))
            {
                RunLongActionInThread(new Action(() =>
                {
                    CallCompletionAction();
                    OnOpenItem();
                }));
            }
        }

        public void CallCompletionAction()
        {
            var tuples = ModeUtils.ModeToTuples(_mode);
            foreach (var path in _filelist.GetList(includeMarked: true))
            {
                if (path.Contains(FilenameUtils.MarkerString))
                {
                    string pathWithoutCategory, category;
                    FilenameUtils.GetCategoryFromFilename(path, out pathWithoutCategory, out category);
                    var tupleFound = tuples.FirstOrDefault((item) => item.Item3 == category);
                    if (tupleFound == null)
                    {
                        Utils.MessageErr("Unknown category for file " + path, true);
                    }
                    else
                    {
                        _mode.OnCompletionAction(_filelist.BaseDirectory, path, pathWithoutCategory, tupleFound);
                    }
                }
            }
        }

        public void RunLongActionInThread(Action fn)
        {
            UIEnable(false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    fn.Invoke();
                }
                finally
                {
                    this.Invoke((MethodInvoker)(() =>
                    {
                        UIEnable(true);
                        OnOpenItem();
                    }));
                }
            });
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (components != null)
                {
                    components.Dispose();
                }

                if (_filelist != null)
                {
                    _filelist.Dispose();
                }

                if (_imagecache != null)
                {
                    _imagecache.Dispose();
                }

                if (_bitmapBlank != null)
                {
                    _bitmapBlank.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
