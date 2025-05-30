﻿using System;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer;

using static FlyleafLib.Utils;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer;

unsafe partial class Player
{
    public bool IsOpenFileDialogOpen    { get; private set; }


    public void SeekBackward()  => SeekBackward_(Config.Player.SeekOffset, Config.Player.SeekOffsetAccurate);
    public void SeekBackward2() => SeekBackward_(Config.Player.SeekOffset2, Config.Player.SeekOffsetAccurate2);
    public void SeekBackward3() => SeekBackward_(Config.Player.SeekOffset3, Config.Player.SeekOffsetAccurate3);
    public void SeekBackward4() => SeekBackward_(Config.Player.SeekOffset4, Config.Player.SeekOffsetAccurate4);
    public void SeekBackward_(long offset, bool accurate)
    {
        if (!CanPlay)
            return;

        long seekTs = CurTime - (CurTime % offset) - offset;

        if (Config.Player.SeekAccurate || accurate)
            SeekAccurate(Math.Max((int) (seekTs / 10000), 0));
        else
            Seek(Math.Max((int) (seekTs / 10000), 0), false);
    }

    public void SeekForward()   => SeekForward_(Config.Player.SeekOffset, Config.Player.SeekOffsetAccurate);
    public void SeekForward2()  => SeekForward_(Config.Player.SeekOffset2, Config.Player.SeekOffsetAccurate2);
    public void SeekForward3()  => SeekForward_(Config.Player.SeekOffset3, Config.Player.SeekOffsetAccurate3);
    public void SeekForward4()  => SeekForward_(Config.Player.SeekOffset4, Config.Player.SeekOffsetAccurate4);
    public void SeekForward_(long offset, bool accurate)
    {
        if (!CanPlay)
            return;

        long seekTs = CurTime - (CurTime % offset) + offset;

        if (seekTs > Duration && !isLive)
            return;

        if (Config.Player.SeekAccurate || accurate)
            SeekAccurate((int)(seekTs / 10000));
        else
            Seek((int)(seekTs / 10000), true);
    }

    public void SeekToChapter(Demuxer.Chapter chapter) =>
        /* TODO
* Accurate pts required (backward/forward check)
* Get current chapter implementation + next/prev
*/
        Seek((int)(chapter.StartTime / 10000.0), true);

    public void CopyToClipboard()
    {
        var url = decoder.Playlist.Url;
        if (url == null)
            return;

        Clipboard.SetText(url);
        OSDMessage = $"Copy {url}";
    }
    public void CopyItemToClipboard()
    {
        if (decoder.Playlist.Selected == null || decoder.Playlist.Selected.DirectUrl == null)
            return;

        string url = decoder.Playlist.Selected.DirectUrl;

        Clipboard.SetText(url);
        OSDMessage = $"Copy {url}";
    }
    public void OpenFromClipboard()
    {
        string text = Clipboard.GetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            OpenAsync(text);
        }
    }

    public void OpenFromClipboardSafe()
    {
        if (decoder.Playlist.Selected != null)
        {
            return;
        }

        OpenFromClipboard();
    }

    public void OpenFromFileDialog()
    {
        int prevTimeout = Activity.Timeout;
        Activity.IsEnabled = false;
        Activity.Timeout = 0;
        IsOpenFileDialogOpen = true;

        System.Windows.Forms.OpenFileDialog openFileDialog = new();

        // If there is currently an open file, set that folder as the base folder
        if (decoder.Playlist.Url != null && File.Exists(decoder.Playlist.Url))
        {
            var folder = Path.GetDirectoryName(decoder.Playlist.Url);
            if (folder != null)
                openFileDialog.InitialDirectory = folder;
        }

        var res = openFileDialog.ShowDialog();

        if (res == System.Windows.Forms.DialogResult.OK)
            OpenAsync(openFileDialog.FileName);

        Activity.Timeout = prevTimeout;
        Activity.IsEnabled = true;
        IsOpenFileDialogOpen = false;
    }

    public void ShowFrame(int frameIndex)
    {
        if (!Video.IsOpened || !CanPlay || VideoDemuxer.IsHLSLive) return;

        lock (lockActions)
        {
            Pause();
            dFrame = null;
            for (int i = 0; i < subNum; i++)
            {
                sFrames[i] = null;
                SubtitleClear(i);
            }

            decoder.Flush();
            decoder.RequiresResync = true;

            vFrame = VideoDecoder.GetFrame(frameIndex);
            if (vFrame == null) return;

            if (CanDebug) Log.Debug($"SFI: {VideoDecoder.GetFrameNumber(vFrame.timestamp)}");

            curTime = vFrame.timestamp;
            renderer.Present(vFrame);
            reversePlaybackResync = true;
            vFrame = null;

            UI(() => UpdateCurTime());
        }
    }

    // Whether video queue should be flushed as it could have opposite direction frames
    bool shouldFlushNext;
    bool shouldFlushPrev;
    public void ShowFrameNext()
    {
        if (!Video.IsOpened || !CanPlay || VideoDemuxer.IsHLSLive)
            return;

        lock (lockActions)
        {
            Pause();

            if (Status == Status.Ended)
            {
                status = Status.Paused;
                UI(() => Status = Status);
            }

            shouldFlushPrev = true;
            decoder.RequiresResync = true;

            if (shouldFlushNext)
            {
                decoder.StopThreads();
                decoder.Flush();
                shouldFlushNext = false;

                var vFrame = VideoDecoder.GetFrame(VideoDecoder.GetFrameNumber(CurTime));
                VideoDecoder.DisposeFrame(vFrame);
            }

            for (int i = 0; i < subNum; i++)
            {
                sFrames[i] = null;
                SubtitleClear(i);
            }

            if (VideoDecoder.Frames.IsEmpty)
                vFrame = VideoDecoder.GetFrameNext();
            else
                VideoDecoder.Frames.TryDequeue(out vFrame);

            if (vFrame == null)
                return;

            if (CanDebug) Log.Debug($"SFN: {VideoDecoder.GetFrameNumber(vFrame.timestamp)}");

            curTime = curTime = vFrame.timestamp;
            renderer.Present(vFrame);
            reversePlaybackResync = true;
            vFrame = null;

            UI(() => UpdateCurTime());
        }
    }
    public void ShowFramePrev()
    {
        if (!Video.IsOpened || !CanPlay || VideoDemuxer.IsHLSLive)
            return;

        lock (lockActions)
        {
            Pause();

            if (Status == Status.Ended)
            {
                status = Status.Paused;
                UI(() => Status = Status);
            }

            shouldFlushNext = true;
            decoder.RequiresResync = true;

            if (shouldFlushPrev)
            {
                decoder.StopThreads();
                decoder.Flush();
                shouldFlushPrev = false;
            }

            for (int i = 0; i < subNum; i++)
            {
                sFrames[i] = null;
                SubtitleClear(i);
            }

            if (VideoDecoder.Frames.IsEmpty)
            {
                // Temp fix for previous timestamps until we seperate GetFrame for Extractor and the Player
                reversePlaybackResync = true;
                int askedFrame = VideoDecoder.GetFrameNumber(CurTime) - 1;
                //Log.Debug($"CurTime1: {TicksToTime(CurTime)}, Asked: {askedFrame}");
                vFrame = VideoDecoder.GetFrame(askedFrame);
                if (vFrame == null) return;

                int recvFrame = VideoDecoder.GetFrameNumber(vFrame.timestamp);
                //Log.Debug($"CurTime2: {TicksToTime(vFrame.timestamp)}, Got: {recvFrame}");
                if (askedFrame != recvFrame)
                {
                    VideoDecoder.DisposeFrame(vFrame);
                    vFrame = null;
                    vFrame = askedFrame > recvFrame
                        ? VideoDecoder.GetFrame(VideoDecoder.GetFrameNumber(CurTime))
                        : VideoDecoder.GetFrame(VideoDecoder.GetFrameNumber(CurTime) - 2);
                }
            }
            else
                VideoDecoder.Frames.TryDequeue(out vFrame);

            if (vFrame == null)
                return;

            if (CanDebug) Log.Debug($"SFB: {VideoDecoder.GetFrameNumber(vFrame.timestamp)}");

            curTime = vFrame.timestamp;
            renderer.Present(vFrame);
            vFrame = null;
            UI(() => UpdateCurTime()); // For some strange reason this will not be updated on KeyDown (only on KeyUp) which doesn't happen on ShowFrameNext (GPU overload? / Thread.Sleep underlying in UI thread?)
        }
    }

    public void SpeedUp()       => Speed += Config.Player.SpeedOffset;
    public void SpeedUp2()      => Speed += Config.Player.SpeedOffset2;
    public void SpeedDown()     => Speed -= Config.Player.SpeedOffset;
    public void SpeedDown2()    => Speed -= Config.Player.SpeedOffset2;

    public void RotateRight()   => renderer.Rotation = (renderer.Rotation + 90) % 360;
    public void RotateLeft()    => renderer.Rotation = renderer.Rotation < 90 ? 360 + renderer.Rotation - 90 : renderer.Rotation - 90;

    public void FullScreen()    => Host?.Player_SetFullScreen(true);
    public void NormalScreen()  => Host?.Player_SetFullScreen(false);
    public void ToggleFullScreen()
    {
        if (Host == null)
            return;

        if (Host.Player_GetFullScreen())
            Host.Player_SetFullScreen(false);
        else
            Host.Player_SetFullScreen(true);
    }

    /// <summary>
    /// Starts recording (uses Config.Player.FolderRecordings and default filename title_curTime)
    /// </summary>
    public void StartRecording()
    {
        if (!CanPlay)
            return;
        try
        {
            if (!Directory.Exists(Config.Player.FolderRecordings))
                Directory.CreateDirectory(Config.Player.FolderRecordings);

            string filename = GetValidFileName(string.IsNullOrEmpty(Playlist.Selected.Title) ? "Record" : Playlist.Selected.Title) + $"_{new TimeSpan(CurTime):hhmmss}." + decoder.Extension;
            filename = FindNextAvailableFile(Path.Combine(Config.Player.FolderRecordings, filename));
            StartRecording(ref filename, false);
        } catch { }
    }

    /// <summary>
    /// Starts recording
    /// </summary>
    /// <param name="filename">Path of the new recording file</param>
    /// <param name="useRecommendedExtension">You can force the output container's format or use the recommended one to avoid incompatibility</param>
    public void StartRecording(ref string filename, bool useRecommendedExtension = true)
    {
        if (!CanPlay)
            return;

        OSDMessage = $"Start recording to {Path.GetFileName(filename)}";
        decoder.StartRecording(ref filename, useRecommendedExtension);
        IsRecording = decoder.IsRecording;
    }

    /// <summary>
    /// Stops recording
    /// </summary>
    public void StopRecording()
    {
        decoder.StopRecording();
        IsRecording = decoder.IsRecording;
        OSDMessage = "Stop recording";
    }
    public void ToggleRecording()
    {
        if (!CanPlay) return;

        if (IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    /// <summary>
    /// <para>Saves the current video frame (encoding based on file extention .bmp, .png, .jpg)</para>
    /// <para>If filename not specified will use Config.Player.FolderSnapshots and with default filename title_frameNumber.ext (ext from Config.Player.SnapshotFormat)</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="filename">Specify the filename (null: will use Config.Player.FolderSnapshots and with default filename title_frameNumber.ext (ext from Config.Player.SnapshotFormat)</param>
    /// <param name="width">Specify the width (-1: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (-1: will keep the ratio based on width)</param>
    /// <param name="frame">Specify the frame (null: will use the current/last frame)</param>
    /// <exception cref="Exception"></exception>
    public void TakeSnapshotToFile(string filename = null, int width = -1, int height = -1, VideoFrame frame = null)
    {
        if (!CanPlay)
            return;

        if (filename == null)
        {
            try
            {
                if (!Directory.Exists(Config.Player.FolderSnapshots))
                    Directory.CreateDirectory(Config.Player.FolderSnapshots);

                // TBR: if frame is specified we don't know the frame's number
                filename = GetValidFileName(string.IsNullOrEmpty(Playlist.Selected.Title) ? "Snapshot" : Playlist.Selected.Title) + $"_{(frame == null ? VideoDecoder.GetFrameNumber(CurTime).ToString() : "X")}.{Config.Player.SnapshotFormat}";
                filename = FindNextAvailableFile(Path.Combine(Config.Player.FolderSnapshots, filename));
            } catch { return; }
        }

        string ext = GetUrlExtention(filename);

        ImageFormat imageFormat;

        switch (ext)
        {
            case "bmp":
                imageFormat = ImageFormat.Bmp;
                break;

            case "png":
                imageFormat = ImageFormat.Png;
                break;

            case "jpg":
            case "jpeg":
                imageFormat = ImageFormat.Jpeg;
                break;

            default:
                throw new Exception($"Invalid snapshot extention '{ext}' (valid .bmp, .png, .jpeg, .jpg");
        }

        if (renderer == null)
            return;

        var snapshotBitmap = renderer.GetBitmap(width, height, frame);
        if (snapshotBitmap == null)
            return;

        Exception e = null;
        try
        {
            snapshotBitmap.Save(filename, imageFormat);

            UI(() =>
            {
                OSDMessage = $"Save snapshot to {Path.GetFileName(filename)}";
            });
        }
        catch (Exception e2)
        {
            e = e2;
        }
        snapshotBitmap.Dispose();

        if (e != null)
            throw e;
    }

    /// <summary>
    /// <para>Returns a bitmap of the current or specified video frame</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="width">Specify the width (-1: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (-1: will keep the ratio based on width)</param>
    /// <param name="frame">Specify the frame (null: will use the current/last frame)</param>
    /// <returns></returns>
    public System.Drawing.Bitmap TakeSnapshotToBitmap(int width = -1, int height = -1, VideoFrame frame = null) => renderer?.GetBitmap(width, height, frame);

    public void ZoomIn()         => Zoom += Config.Player.ZoomOffset;
    public void ZoomOut()       { if (Zoom - Config.Player.ZoomOffset < 1) return; Zoom -= Config.Player.ZoomOffset; }

    /// <summary>
    /// Pan zoom in with center point
    /// </summary>
    /// <param name="p"></param>
    public void ZoomIn(Point p) { renderer.ZoomWithCenterPoint(p, renderer.Zoom + Config.Player.ZoomOffset / 100.0); RaiseUI(nameof(Zoom)); }

    /// <summary>
    /// Pan zoom out with center point
    /// </summary>
    /// <param name="p"></param>
    public void ZoomOut(Point p){ double zoom = renderer.Zoom - Config.Player.ZoomOffset / 100.0; if (zoom < 0.001) return; renderer.ZoomWithCenterPoint(p, zoom); RaiseUI(nameof(Zoom)); }

    /// <summary>
    /// Pan zoom (no raise)
    /// </summary>
    /// <param name="zoom"></param>
    public void SetZoom(double zoom) => renderer.SetZoom(zoom);
    /// <summary>
    /// Pan zoom's center point (no raise, no center point change)
    /// </summary>
    /// <param name="p"></param>
    public void SetZoomCenter(Point p) => renderer.SetZoomCenter(p);
    /// <summary>
    /// Pan zoom and center point (no raise)
    /// </summary>
    /// <param name="zoom"></param>
    /// <param name="p"></param>
    public void SetZoomAndCenter(double zoom, Point p) => renderer.SetZoomAndCenter(zoom, p);

    public void ResetAll()
    {
        ResetSpeed();
        ResetRotation();
        ResetZoom();
    }

    public void ResetSpeed()
    {
        Speed = 1;
    }

    public void ResetRotation()
    {
        Rotation = 0;
    }

    public void ResetZoom()
    {
        bool npx = renderer.PanXOffset != 0;
        bool npy = renderer.PanYOffset != 0;
        bool npz = renderer.Zoom != 1;
        renderer.SetPanAll(0, 0, Rotation, 1, Renderer.ZoomCenterPoint, true); // Pan X/Y, Rotation, Zoom, Zoomcenter, Refresh

        UI(() =>
        {
            if (npx) Raise(nameof(PanXOffset));
            if (npy) Raise(nameof(PanYOffset));
            if (npz) Raise(nameof(Zoom));
        });
    }
}
