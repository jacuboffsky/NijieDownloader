﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using FirstFloor.ModernUI;
using FirstFloor.ModernUI.Windows;
using FirstFloor.ModernUI.Windows.Controls;
using Nandaka.Common;

namespace NijieDownloader.UI.Settings
{
    /// <summary>
    /// Interaction logic for Network.xaml
    /// </summary>
    public partial class Download : UserControl, IContent
    {
        public List<String> LogLevel { get; set; }

        private int _oldBatch;
        private int _oldThumb;
        private bool _isChanged;

        private MainWindow mainWindow;

        #region commands

        public static readonly DependencyProperty DeleteItemCommandProperty =
            DependencyProperty.Register("DeleteItemCommand", typeof(RoutedCommand), typeof(UserControl));

        public RoutedCommand DeleteItemCommand
        {
            get { return (RoutedCommand)GetValue(DeleteItemCommandProperty); }
            set { SetValue(DeleteItemCommandProperty, value); }
        }

        private void ExecuteDeleteItemCommand(object sender, ExecutedRoutedEventArgs e)
        {
            var UiItem = e.OriginalSource as Control;
            var str = UiItem.DataContext.ToString();

            if (mainWindow.FormatList.Contains(str))
            {
                mainWindow.FormatList.Remove(str);
            }
            e.Handled = true;
        }

        private void CanExecuteDeleteItemCommand(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        #endregion commands

        public Download()
        {
            InitializeComponent();
            LogLevel = new List<string>(new string[] { "Off", "Fatal", "Error", "Warn", "Info", "Debug", "All" });
            cbxLogLevel.DataContext = this;

            _oldBatch = Properties.Settings.Default.ConcurrentJob;
            _oldThumb = Properties.Settings.Default.ConcurrentImageLoad;
            _isChanged = false;

            mainWindow = Application.Current.MainWindow as MainWindow;

            this.CommandBindings.Add(new CommandBinding((DeleteItemCommand = new RoutedCommand()), ExecuteDeleteItemCommand, CanExecuteDeleteItemCommand));

            this.DataContext = this;
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        private void SaveSettings()
        {
            mainWindow.ValidateFormatList(new string[] { Properties.Settings.Default.FilenameFormat, Properties.Settings.Default.MangaFilenameFormat, Properties.Settings.Default.AvatarFilenameFormat });

            Properties.Settings.Default.FormatList.Clear();
            Properties.Settings.Default.FormatList.AddRange(mainWindow.FormatList.ToArray());

            MainWindow.SaveAllSettings();
            MainWindow.Log.Logger.Repository.Threshold = MainWindow.Log.Logger.Repository.LevelMap[Properties.Settings.Default.LogLevel];

            if (_oldBatch != Properties.Settings.Default.ConcurrentJob ||
                _oldThumb != Properties.Settings.Default.ConcurrentImageLoad)
            {
                ModernDialog d = new ModernDialog();
                d.Content = "Restart Required!";
                d.ShowDialog();
            }

            SetPreventSleep();
            _isChanged = false;
            Properties.Settings.Default.Reload();
        }

        public static void SetPreventSleep()
        {
            if (Properties.Settings.Default.PreventSleep)
            {
                Util.NativeMethods.SetThreadExecutionState(Util.NativeMethods.ES_AWAYMODE_REQUIRED | Util.NativeMethods.ES_CONTINUOUS);
            }
            else
            {
                Util.NativeMethods.SetThreadExecutionState(Util.NativeMethods.ES_AWAYMODE_REQUIRED);
            }
        }

        private void Hyperlink_Click(object sender, RoutedEventArgs e)
        {
            if (pnlFilenameHelp.Visibility == System.Windows.Visibility.Collapsed)
                pnlFilenameHelp.Visibility = System.Windows.Visibility.Visible;
            else
                pnlFilenameHelp.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Reset();
        }

        private void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Reset();
            var result = Util.DeleteUserSettings();
            Properties.Settings.Default.Reload();
            if (!string.IsNullOrWhiteSpace(result))
            {
                MessageBox.Show(string.Format("Some items cannot be deleted: {0}", result));
            }
        }

        private void btnOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(txtRootDir.Text);
                if (!dir.Exists)
                    dir.Create();

                Process.Start(dir.FullName);
            }
            catch (Exception ex)
            {
                ModernDialog d = new ModernDialog();
                d.Content = ex.Message;
                d.ShowDialog();
                MainWindow.Log.Error(string.Format("Failed to open root directory: {0}", txtRootDir.Text), ex);
            }
        }

        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog f = new System.Windows.Forms.FolderBrowserDialog();
            f.ShowNewFolderButton = true;
            var result = f.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                txtRootDir.Text = f.SelectedPath;
            }
        }

        private void StackPanel_SourceUpdated(object sender, DataTransferEventArgs e)
        {
            Console.WriteLine(String.Format("{0} => {1} :> {2}", e.Property.Name, e.TargetObject, e.TargetObject));
            _isChanged = true;
        }

        #region nav

        public void OnFragmentNavigation(FirstFloor.ModernUI.Windows.Navigation.FragmentNavigationEventArgs e)
        {
        }

        public void OnNavigatedFrom(FirstFloor.ModernUI.Windows.Navigation.NavigationEventArgs e)
        {
        }

        public void OnNavigatedTo(FirstFloor.ModernUI.Windows.Navigation.NavigationEventArgs e)
        {
        }

        public void OnNavigatingFrom(FirstFloor.ModernUI.Windows.Navigation.NavigatingCancelEventArgs e)
        {
            if (_isChanged)
            {
                var result = MessageBox.Show("Settings has been changed, save?", "Download Settings", MessageBoxButton.YesNoCancel);
                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                }
                else if (result == MessageBoxResult.Yes)
                {
                    SaveSettings();
                }
            }
        }

        #endregion nav
    }
}