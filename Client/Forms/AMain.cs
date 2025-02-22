﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using Client;
using System.Linq;
using Microsoft.Web.WebView2.Core;

namespace Launcher
{
    public partial class AMain : Form
    {
        long _totalBytes, _completedBytes, _currentBytes;
        private int _fileCount, _currentCount;

        private FileInformation _currentFile;
        public bool Completed, Checked, CleanFiles, LabelSwitch, ErrorFound;
        
        public List<FileInformation> OldList;
        public Queue<FileInformation> DownloadList;

        private Stopwatch _stopwatch = Stopwatch.StartNew();

        public Thread _workThread;

        private bool dragging = false;
        private Point dragCursorPoint;
        private Point dragFormPoint;

        private Config ConfigForm = new Config();

        private bool Restart = false;

        public AMain()
        {
            InitializeComponent();

            BackColor = Color.FromArgb(1, 0, 0);
            TransparencyKey = Color.FromArgb(1, 0, 0);
        }

        public static void SaveError(string ex)
        {
            try
            {
                if (Settings.RemainingErrorLogs-- > 0)
                {
                    File.AppendAllText(@".\Error.txt",
                                       string.Format("[{0}] {1}{2}", DateTime.Now, ex, Environment.NewLine));
                }
            }
            catch
            {
            }
        }

        public void Start()
        {
            try
            {
                DownloadList = new Queue<FileInformation>();

                GetOldFileList();

                if (OldList.Count == 0)
                {
                    MessageBox.Show(GameLanguage.PatchErr);
                    Completed = true;
                    return;
                }

                _fileCount = OldList.Count;
                for (int i = 0; i < OldList.Count; i++)
                    CheckFile(OldList[i]);

                Checked = true;
                _fileCount = 0;
                _currentCount = 0;


                _fileCount = DownloadList.Count;
                BeginDownload();
            }
            catch (EndOfStreamException ex)
            {
                MessageBox.Show("End of stream found. Host is likely using a pre version 1.1.0.0 patch system");
                Completed = true;
                SaveError(ex.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error");
                Completed = true;
                SaveError(ex.ToString());
            }
        }

        

        private void BeginDownload()
        {           
            if (DownloadList == null) return;

            if (DownloadList.Count == 0)
            {
                DownloadList = null;
                _currentFile = null;
                Completed = true;

                CleanUp();
                return;
            }

            _currentFile = DownloadList.Dequeue();

            Download(_currentFile);
        }
        private void CleanUp()
        {
            if (!CleanFiles) return;

            string[] fileNames = Directory.GetFiles(@".\", "*.*", SearchOption.AllDirectories);
            string fileName;
            for (int i = 0; i < fileNames.Length; i++)
            {
                if (fileNames[i].StartsWith(".\\Screenshots\\")) continue;

                fileName = Path.GetFileName(fileNames[i]);

                if (fileName == "Mir2Config.ini" || fileName == System.AppDomain.CurrentDomain.FriendlyName) continue;

                try
                {
                    if (!NeedFile(fileNames[i]))
                        File.Delete(fileNames[i]);
                }
                catch{}
            }
        }
        public bool NeedFile(string fileName)
        {
            for (int i = 0; i < OldList.Count; i++)
            {
                if (fileName.EndsWith(OldList[i].FileName))
                    return true;
            }

            return false;
        }

        private void GetOldFileList()
        {
            OldList = new List<FileInformation>();

            //byte[] data = DownloadFile(PatchFileName);
            byte[] data = Download(Settings.P_PatchFileName);

            if (data != null)
            {
                using MemoryStream stream = new MemoryStream(data);
                using BinaryReader reader = new BinaryReader(stream);

                int count = reader.ReadInt32();

                for (int i = 0; i < count; i++)
                {
                    OldList.Add(new FileInformation(reader));
                }
            }
        }


        public void ParseOld(BinaryReader reader)
        {
            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
                OldList.Add(new FileInformation(reader));
        }

        public void CheckFile(FileInformation old)
        {
            FileInformation info = GetFileInformation(Settings.P_Client + old.FileName);
            _currentCount++;

            if (info == null || old.Length != info.Length || old.Creation != info.Creation)
            {
                if (info != null && (Path.GetExtension(old.FileName).ToLower() == ".dll" || Path.GetExtension(old.FileName).ToLower() == ".exe"))
                {
                    string oldFilename = Path.Combine(Path.GetDirectoryName(old.FileName), ("Old__" + Path.GetFileName(old.FileName)));

                    try
                    {
                        File.Move(Settings.P_Client + old.FileName, oldFilename);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        SaveError(ex.ToString());
                    }
                    finally
                    {
                        //Might cause an infinite loop if it can never gain access
                        Restart = true;
                    }
                }

                DownloadList.Enqueue(old);
                _totalBytes += old.Length;
            }
        }

        public void Download(FileInformation info)
        {
            string fileName = info.FileName.Replace(@"\", "/");

            if (fileName != "PList.gz" && (info.Compressed != info.Length || info.Compressed == 0))
            {
                fileName += ".gz";
            }

            try
            {
                using (WebClient client = new WebClient())
                {
                    client.DownloadProgressChanged += (o, e) =>
                        {
                            _currentBytes = e.BytesReceived;
                        };
                    client.DownloadDataCompleted += (o, e) =>
                        {
                            if (e.Error != null)
                            {
                                File.AppendAllText(@".\Error.txt",
                                       string.Format("[{0}] {1}{2}", DateTime.Now, info.FileName + " could not be downloaded. (" + e.Error.Message + ")", Environment.NewLine));
                                ErrorFound = true;
                            }
                            else
                            {
                                _currentCount++;
                                _completedBytes += _currentBytes;
                                _currentBytes = 0;
                                _stopwatch.Stop();

                                byte[] raw = e.Result;

                                if (info.Compressed > 0 && info.Compressed != info.Length)
                                {
                                    raw = Decompress(e.Result);
                                }

                                if (!Directory.Exists(Settings.P_Client + Path.GetDirectoryName(info.FileName)))
                                {
                                    Directory.CreateDirectory(Settings.P_Client + Path.GetDirectoryName(info.FileName));
                                }

                                File.WriteAllBytes(Settings.P_Client + info.FileName, raw);
                                File.SetLastWriteTime(Settings.P_Client + info.FileName, info.Creation);
                            }
                            BeginDownload();
                        };

                    if (Settings.P_NeedLogin) client.Credentials = new NetworkCredential(Settings.P_Login, Settings.P_Password);


                    _stopwatch = Stopwatch.StartNew();
                    client.DownloadDataAsync(new Uri(Settings.P_Host + fileName));
                }
            }
            catch
            {
                MessageBox.Show(string.Format("Failed to download file: {0}", fileName));
            }
        }

        public byte[] Download(string fileName)
        {
            string authInfo = Settings.P_Login + ":" + Settings.P_Password;
            authInfo = Convert.ToBase64String(System.Text.Encoding.Default.GetBytes(authInfo));

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Settings.P_Host + Path.ChangeExtension(fileName, ".gz"));
            request.Method = "GET";
            request.Accept = "application/json; charset=utf-8";

            if (Settings.P_NeedLogin)
                request.Headers["Authorization"] = "Basic " + authInfo;

            var response = (HttpWebResponse)request.GetResponse();

            MemoryStream ms = new MemoryStream();
            response.GetResponseStream().CopyTo(ms);

            byte[] data = ms.ToArray();

            return data;
        }

        //Seems to want to cache the PList when using WebClient, so causes issues. No longer used.
        public byte[] DownloadOld(string fileName)
        {
            fileName = fileName.Replace(@"\", "/");

            if (fileName != "PList.gz")
                fileName += Path.GetExtension(fileName);

            try
            {
                using WebClient client = new WebClient();

                client.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);

                if (Settings.P_NeedLogin)
                {
                    client.Credentials = new NetworkCredential(Settings.P_Login, Settings.P_Password);
                }
                else
                {
                    client.Credentials = new NetworkCredential("", "");
                }

                var rand = new Random(1000).Next();

                return client.DownloadData(Settings.P_Host + Path.ChangeExtension(fileName, ".gz") + $"?rand={rand}");
            }
            catch
            {
                return null;
            }
        }

        public static byte[] Decompress(byte[] raw)
        {
            using (GZipStream gStream = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress))
            {
                const int size = 4096; //4kb
                byte[] buffer = new byte[size];
                using (MemoryStream mStream = new MemoryStream())
                {
                    int count;
                    do
                    {
                        count = gStream.Read(buffer, 0, size);
                        if (count > 0)
                        {
                            mStream.Write(buffer, 0, count);
                        }
                    } while (count > 0);
                    return mStream.ToArray();
                }
            }
        }

        public static byte[] Compress(byte[] raw)
        {
            using (MemoryStream mStream = new MemoryStream())
            {
                using (GZipStream gStream = new GZipStream(mStream, CompressionMode.Compress, true))
                    gStream.Write(raw, 0, raw.Length);
                return mStream.ToArray();
            }
        }

        public FileInformation GetFileInformation(string fileName)
        {
            if (!File.Exists(fileName)) return null;

            FileInfo info = new FileInfo(fileName);
            return new FileInformation
            {
                FileName = fileName.Remove(0, Settings.P_Client.Length),
                Length = (int)info.Length,
                Creation = info.LastWriteTime
            };
        }

        private void AMain_Load(object sender, EventArgs e)
        {
            var envir = CoreWebView2Environment.CreateAsync(null, Settings.ResourcePath).Result;
            Main_browser.EnsureCoreWebView2Async(envir);

            if (Settings.P_BrowserAddress != "")
            {
                Main_browser.NavigationCompleted += Main_browser_NavigationCompleted;
                Main_browser.Source = new Uri(Settings.P_BrowserAddress);
            }

            RepairOldFiles();

            Launch_pb.Enabled = false;
            ProgressCurrent_pb.Width = 5;
            TotalProg_pb.Width = 5;
            Version_label.Text = string.Format("Build: {0}.{1}.{2}", Globals.ProductCodename, Settings.UseTestConfig ? "Debug" : "Release", Application.ProductVersion);

            if (Settings.P_ServerName != String.Empty)
            {
                Name_label.Visible = true;
                Name_label.Text = Settings.P_ServerName;
            }

            _workThread = new Thread(Start) { IsBackground = true };
            _workThread.Start();
        }

        private void Main_browser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (Main_browser.Source.AbsolutePath != "blank") Main_browser.Visible = true;
        }

        private void Launch_pb_Click(object sender, EventArgs e)
        {
            Launch();
        }

        private void Launch()
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
            Program.Form = new CMain();
            Program.Form.Closed += (s, args) => this.Close();
            Program.Form.Show();
            Program.PForm.Hide();
        }

        private void Close_pb_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
            Close();
        }

        private void Movement_panel_MouseClick(object sender, MouseEventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
            dragging = true;
            dragCursorPoint = Cursor.Position;
            dragFormPoint = this.Location;
        }

        private void Movement_panel_MouseUp(object sender, MouseEventArgs e)
        {
            dragging = false;
        }

        private void Movement_panel_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                Point dif = Point.Subtract(Cursor.Position, new Size(dragCursorPoint));
                this.Location = Point.Add(dragFormPoint, new Size(dif));
            }
        }

        private void Launch_pb_MouseEnter(object sender, EventArgs e)
        {
            Launch_pb.Image = Client.Properties.Resources.Launch_Hover;
        }

        private void Launch_pb_MouseLeave(object sender, EventArgs e)
        {
            Launch_pb.Image = Client.Properties.Resources.Launch_Base1;
        }

        private void Close_pb_MouseEnter(object sender, EventArgs e)
        {
            Close_pb.Image = Client.Properties.Resources.Cross_Hover;
        }

        private void Close_pb_MouseLeave(object sender, EventArgs e)
        {
            Close_pb.Image = Client.Properties.Resources.Cross_Base;
        }

        private void Launch_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Launch_pb.Image = Client.Properties.Resources.Launch_Pressed;
        }

        private void Launch_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Launch_pb.Image = Client.Properties.Resources.Launch_Base1;
        }

        private void Close_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Close_pb.Image = Client.Properties.Resources.Cross_Pressed;
        }

        private void Close_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Close_pb.Image = Client.Properties.Resources.Cross_Base;
        }

        private void ProgressCurrent_pb_SizeChanged(object sender, EventArgs e)
        {
            ProgEnd_pb.Location = new Point((ProgressCurrent_pb.Location.X + ProgressCurrent_pb.Width), 490);
            if (ProgressCurrent_pb.Width == 0) ProgEnd_pb.Visible = false;
            else ProgEnd_pb.Visible = true;
        }

        private void Config_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Config_pb.Image = Client.Properties.Resources.Config_Pressed;
        }

        private void Config_pb_MouseEnter(object sender, EventArgs e)
        {
            Config_pb.Image = Client.Properties.Resources.Config_Hover;
        }

        private void Config_pb_MouseLeave(object sender, EventArgs e)
        {
            Config_pb.Image = Client.Properties.Resources.Config_Base;
        }

        private void Config_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Config_pb.Image = Client.Properties.Resources.Config_Base;
        }

        private void Config_pb_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Hide();
            else ConfigForm.Show(Program.PForm);
            ConfigForm.Location = new Point(Location.X + Config_pb.Location.X - 183, Location.Y + 36);
        }

        private void TotalProg_pb_SizeChanged(object sender, EventArgs e)
        {
            ProgTotalEnd_pb.Location = new Point((TotalProg_pb.Location.X + TotalProg_pb.Width), 508);
            if (TotalProg_pb.Width == 0) ProgTotalEnd_pb.Visible = false;
            else ProgTotalEnd_pb.Visible = true;
        }

        private void InterfaceTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (Completed)
                {
                    
                    ActionLabel.Text = "";
                    CurrentFile_label.Text = "Up to date.";
                    SpeedLabel.Text = "";
                    ProgressCurrent_pb.Width = 550;
                    TotalProg_pb.Width = 550;
                    CurrentFile_label.Visible = true;
                    CurrentPercent_label.Visible = true;
                    TotalPercent_label.Visible = true;
                    CurrentPercent_label.Text = "100%";
                    TotalPercent_label.Text = "100%";
                    InterfaceTimer.Enabled = false;
                    Launch_pb.Enabled = true;
                    if (ErrorFound) MessageBox.Show("One or more files failed to download, check Error.txt for details.", "Failed to Download.");
                    ErrorFound = false;

                    if (CleanFiles)
                    {
                        CleanFiles = false;
                        MessageBox.Show("Your files have been cleaned up.", "Clean Files");
                    }

                    if (Restart)
                    {
                        Program.Restart = true;

                        MoveOldFilesToCurrent();

                        Close();
                    }

                    if (Settings.P_AutoStart)
                    {
                        Launch();
                    }
                    return;
                }

                ActionLabel.Visible = true;
                SpeedLabel.Visible = true;
                CurrentFile_label.Visible = true;
                CurrentPercent_label.Visible = true;
                TotalPercent_label.Visible = true;

                if (LabelSwitch) ActionLabel.Text = string.Format("{0} Files Remaining", _fileCount - _currentCount);
                else ActionLabel.Text = string.Format("{0:#,##0}MB Remaining",  ((_totalBytes) - (_completedBytes + _currentBytes)) / 1024 / 1024);

                //ActionLabel.Text = string.Format("{0:#,##0}MB / {1:#,##0}MB", (_completedBytes + _currentBytes) / 1024 / 1024, _totalBytes / 1024 / 1024);

                if (_currentFile != null)
                {
                    //FileLabel.Text = string.Format("{0}, ({1:#,##0} MB) / ({2:#,##0} MB)", _currentFile.FileName, _currentBytes / 1024 / 1024, _currentFile.Compressed / 1024 / 1024);
                    CurrentFile_label.Text = string.Format("{0}", _currentFile.FileName);
                    SpeedLabel.Text = (_currentBytes / 1024F / _stopwatch.Elapsed.TotalSeconds).ToString("#,##0.##") + "KB/s";
                    CurrentPercent_label.Text = ((int)(100 * _currentBytes / _currentFile.Length)).ToString() + "%";
                    ProgressCurrent_pb.Width = (int)( 5.5 * (100 * _currentBytes / _currentFile.Length));
                }
                TotalPercent_label.Text = ((int)(100 * (_completedBytes + _currentBytes) / _totalBytes)).ToString() + "%";
                TotalProg_pb.Width = (int)(5.5 * (100 * (_completedBytes + _currentBytes) / _totalBytes));
            }
            catch (Exception ex)
            {
                
            }

        }

        private void AMain_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
        }

        private void ActionLabel_Click(object sender, EventArgs e)
        {
            LabelSwitch = !LabelSwitch;
        }

        private void Credit_label_Click(object sender, EventArgs e)
        {
            if (Credit_label.Text == "Powered by Crystal M2") Credit_label.Text = "Designed by Breezer";
            else Credit_label.Text = "Powered by Crystal M2";
        }

        private void AMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            MoveOldFilesToCurrent();
        }

        private void RepairOldFiles()
        {
            var files = Directory.GetFiles(Settings.P_Client, "*", SearchOption.AllDirectories).Where(x => Path.GetFileName(x).StartsWith("Old__"));

            foreach (var oldFilename in files)
            {
                if (!File.Exists(oldFilename.Replace("Old__", "")))
                {
                    File.Move(oldFilename, oldFilename.Replace("Old__", ""));
                }
                else
                {
                    File.Delete(oldFilename);
                }
            }
        }

        private void MoveOldFilesToCurrent()
        {
            var files = Directory.GetFiles(Settings.P_Client, "*", SearchOption.AllDirectories).Where(x => Path.GetFileName(x).StartsWith("Old__"));

            foreach (var oldFilename in files)
            {
                string originalFilename = Path.Combine(Path.GetDirectoryName(oldFilename), (Path.GetFileName(oldFilename).Replace("Old__", "")));

                if (!File.Exists(originalFilename) && File.Exists(oldFilename))
                    File.Move(oldFilename, originalFilename);
            }
        }
    }

    public class FileInformation
    {
        public string FileName; //Relative.
        public int Length, Compressed;
        public DateTime Creation;

        public FileInformation()
        {

        }
        public FileInformation(BinaryReader reader)
        {
            FileName = reader.ReadString();
            Length = reader.ReadInt32();
            Compressed = reader.ReadInt32();

            Creation = DateTime.FromBinary(reader.ReadInt64());
        }
        public void Save(BinaryWriter writer)
        {
            writer.Write(FileName);
            writer.Write(Length);
            writer.Write(Compressed);
            writer.Write(Creation.ToBinary());
        }
    }
}
