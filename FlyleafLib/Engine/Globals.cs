﻿global using System;
global using Flyleaf.FFmpeg;
global using static Flyleaf.FFmpeg.Raw;

using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace FlyleafLib;

public enum PixelFormatType
{
    Hardware,
    Software_Handled,
    Software_Sws
}
public enum MediaType
{
    Audio,
    Video,
    Subs,
    Data
}
public enum InputType
{
    File    = 0,
    UNC     = 1,
    Torrent = 2,
    Web     = 3,
    Unknown = 4
}
public enum HDRtoSDRMethod : int
{
    None        = 0,
    Aces        = 1,
    Hable       = 2,
    Reinhard    = 3
}
public enum VideoProcessors
{
    Auto,
    D3D11,
    Flyleaf,
}
public enum ZeroCopy : int
{
    Auto        = 0,
    Enabled     = 1,
    Disabled    = 2
}
public enum ColorSpace : int
{
    None        = 0,
    BT601       = 1,
    BT709       = 2,
    BT2020      = 3
}
public enum ColorRange : int
{
    None        = 0,
    Full        = 1,
    Limited     = 2
}

public enum SubOCREngineType
{
    Tesseract,
    MicrosoftOCR
}

public enum SubASREngineType
{
    [Description("whisper.cpp")]
    WhisperCpp,
    [Description("faster-whisper (Recommended)")]
    FasterWhisper
}

public class GPUOutput
{
    public static int GPUOutputIdGenerator;

    public int      Id          { get; set; }
    public string   DeviceName  { get; internal set; }
    public int      Left        { get; internal set; }
    public int      Top         { get; internal set; }
    public int      Right       { get; internal set; }
    public int      Bottom      { get; internal set; }
    public int      Width       => Right- Left;
    public int      Height      => Bottom- Top;
    public bool     IsAttached  { get; internal set; }
    public int      Rotation    { get; internal set; }

    public override string ToString()
    {
        int gcd = Utils.GCD(Width, Height);
        return $"{DeviceName,-20} [Id: {Id,-4}\t, Top: {Top,-4}, Left: {Left,-4}, Width: {Width,-4}, Height: {Height,-4}, Ratio: [" + (gcd > 0 ? $"{Width/gcd}:{Height/gcd}]" : "]");
    }
}

public class GPUAdapter
{
    public int      MaxHeight       { get; internal set; }
    public nuint    SystemMemory    { get; internal set; }
    public nuint    VideoMemory     { get; internal set; }
    public nuint    SharedMemory    { get; internal set; }


    public uint     Id              { get; internal set; }
    public string   Vendor          { get; internal set; }
    public string   Description     { get; internal set; }
    public long     Luid            { get; internal set; }
    public bool     HasOutput       { get; internal set; }
    public List<GPUOutput>
                    Outputs         { get; internal set; }

    public override string ToString()
        => (Vendor + " " + Description).PadRight(40) + $"[ID: {Id,-6}, LUID: {Luid,-6}, DVM: {Utils.GetBytesReadable(VideoMemory),-8}, DSM: {Utils.GetBytesReadable(SystemMemory),-8}, SSM: {Utils.GetBytesReadable(SharedMemory)}]";
}
public enum VideoFilters
{
    // Ensure we have the same values with Vortice.Direct3D11.VideoProcessorFilterCaps (d3d11.h) | we can extended if needed with other values

    Brightness          = 0x01,
    Contrast            = 0x02,
    Hue                 = 0x04,
    Saturation          = 0x08,
    NoiseReduction      = 0x10,
    EdgeEnhancement     = 0x20,
    AnamorphicScaling   = 0x40,
    StereoAdjustment    = 0x80
}

public struct AspectRatio : IEquatable<AspectRatio>
{
    public static readonly AspectRatio Keep     = new(-1, 1);
    public static readonly AspectRatio Fill     = new(-2, 1);
    public static readonly AspectRatio Custom   = new(-3, 1);
    public static readonly AspectRatio Invalid  = new(-999, 1);

    public static readonly List<AspectRatio> AspectRatios = new()
    {
        Keep,
        Fill,
        Custom,
        new AspectRatio(1, 1),
        new AspectRatio(4, 3),
        new AspectRatio(16, 9),
        new AspectRatio(16, 10),
        new AspectRatio(2.35f, 1),
    };

    public static implicit operator AspectRatio(string value) => new AspectRatio(value);

    public float Num { get; set; }
    public float Den { get; set; }

    public float Value
    {
        get => Num / Den;
        set  { Num = value; Den = 1; }
    }

    public string ValueStr
    {
        get => ToString();
        set => FromString(value);
    }

    public AspectRatio(float value) : this(value, 1) { }
    public AspectRatio(float num, float den) { Num = num; Den = den; }
    public AspectRatio(string value) { Num = Invalid.Num; Den = Invalid.Den; FromString(value); }

    public bool Equals(AspectRatio other) => Num == other.Num && Den == other.Den;
    public override bool Equals(object obj) => obj is AspectRatio o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Num, Den);
    public static bool operator ==(AspectRatio a, AspectRatio b) => a.Equals(b);
    public static bool operator !=(AspectRatio a, AspectRatio b) => !(a == b);

    public void FromString(string value)
    {
        if (value == "Keep")
            { Num = Keep.Num; Den = Keep.Den; return; }
        else if (value == "Fill")
            { Num = Fill.Num; Den = Fill.Den; return; }
        else if (value == "Custom")
            { Num = Custom.Num; Den = Custom.Den; return; }
        else if (value == "Invalid")
            { Num = Invalid.Num; Den = Invalid.Den; return; }

        string newvalue = value.ToString().Replace(',', '.');

        if (Regex.IsMatch(newvalue.ToString(), @"^\s*[0-9\.]+\s*[:/]\s*[0-9\.]+\s*$"))
        {
            string[] values = newvalue.ToString().Split(':');
            if (values.Length < 2)
                        values = newvalue.ToString().Split('/');

            Num = float.Parse(values[0], NumberStyles.Any, CultureInfo.InvariantCulture);
            Den = float.Parse(values[1], NumberStyles.Any, CultureInfo.InvariantCulture);
        }

        else if (float.TryParse(newvalue.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
            { Num = result; Den = 1; }

        else
            { Num = Invalid.Num; Den = Invalid.Den; }
    }
    public override string ToString() => this == Keep ? "Keep" : (this == Fill ? "Fill" : (this == Custom ? "Custom" : (this == Invalid ? "Invalid" : $"{Num}:{Den}")));
}

class PlayerStats
{
    public long TotalBytes      { get; set; }
    public long VideoBytes      { get; set; }
    public long AudioBytes      { get; set; }
    public long FramesDisplayed { get; set; }
}
public class NotifyPropertyChanged : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    //public bool DisableNotifications { get; set; }

    //private static bool IsUI() => System.Threading.Thread.CurrentThread.ManagedThreadId == System.Windows.Application.Current.Dispatcher.Thread.ManagedThreadId;

    protected bool Set<T>(ref T field, T value, bool check = true, [CallerMemberName] string propertyName = "")
    {
        //Utils.Log($"[===| {propertyName} |===] | Set | {IsUI()}");

        if (!check || !EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;

            //if (!DisableNotifications)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            return true;
        }

        return false;
    }

    protected bool SetUI<T>(ref T field, T value, bool check = true, [CallerMemberName] string propertyName = "")
    {
        //Utils.Log($"[===| {propertyName} |===] | SetUI | {IsUI()}");

        if (!check || !EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;

            //if (!DisableNotifications)
            Utils.UI(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));

            return true;
        }

        return false;
    }
    protected void Raise([CallerMemberName] string propertyName = "")
    {
        //Utils.Log($"[===| {propertyName} |===] | Raise | {IsUI()}");

        //if (!DisableNotifications)
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }


    protected void RaiseUI([CallerMemberName] string propertyName = "")
    {
        //Utils.Log($"[===| {propertyName} |===] | RaiseUI | {IsUI()}");

        //if (!DisableNotifications)
        Utils.UI(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }
}
