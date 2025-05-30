﻿using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using Vortice.DXGI;
using Vortice.Direct3D11;

using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;

using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;

using static FlyleafLib.Logger;
using static FlyleafLib.Utils;

namespace FlyleafLib.MediaFramework.MediaRenderer;

unsafe public partial class Renderer
{
    /* TODO
     * 1) Try to sync filters between Flyleaf and D3D11 video processors so we will not have to reset on change
     * 2) Filter default values will change when the device/adapter is changed
     */

    public static Dictionary<string, VideoProcessorCapsCache> VideoProcessorsCapsCache = new();

    internal static VideoProcessorFilter ConvertFromVideoProcessorFilterCaps(VideoProcessorFilterCaps filter)
    {
        switch (filter)
        {
            case VideoProcessorFilterCaps.Brightness:
                return VideoProcessorFilter.Brightness;
            case VideoProcessorFilterCaps.Contrast:
                return VideoProcessorFilter.Contrast;
            case VideoProcessorFilterCaps.Hue:
                return VideoProcessorFilter.Hue;
            case VideoProcessorFilterCaps.Saturation:
                return VideoProcessorFilter.Saturation;
            case VideoProcessorFilterCaps.EdgeEnhancement:
                return VideoProcessorFilter.EdgeEnhancement;
            case VideoProcessorFilterCaps.NoiseReduction:
                return VideoProcessorFilter.NoiseReduction;
            case VideoProcessorFilterCaps.AnamorphicScaling:
                return VideoProcessorFilter.AnamorphicScaling;
            case VideoProcessorFilterCaps.StereoAdjustment:
                return VideoProcessorFilter.StereoAdjustment;

            default:
                return VideoProcessorFilter.StereoAdjustment;
        }
    }
    internal static VideoProcessorFilterCaps ConvertFromVideoProcessorFilter(VideoProcessorFilter filter)
    {
        switch (filter)
        {
            case VideoProcessorFilter.Brightness:
                return VideoProcessorFilterCaps.Brightness;
            case VideoProcessorFilter.Contrast:
                return VideoProcessorFilterCaps.Contrast;
            case VideoProcessorFilter.Hue:
                return VideoProcessorFilterCaps.Hue;
            case VideoProcessorFilter.Saturation:
                return VideoProcessorFilterCaps.Saturation;
            case VideoProcessorFilter.EdgeEnhancement:
                return VideoProcessorFilterCaps.EdgeEnhancement;
            case VideoProcessorFilter.NoiseReduction:
                return VideoProcessorFilterCaps.NoiseReduction;
            case VideoProcessorFilter.AnamorphicScaling:
                return VideoProcessorFilterCaps.AnamorphicScaling;
            case VideoProcessorFilter.StereoAdjustment:
                return VideoProcessorFilterCaps.StereoAdjustment;

            default:
                return VideoProcessorFilterCaps.StereoAdjustment;
        }
    }
    internal static VideoFilter ConvertFromVideoProcessorFilterRange(VideoProcessorFilterRange filter) => new()
    {
        Minimum = filter.Minimum,
        Maximum = filter.Maximum,
        Value   = filter.Default,
        Step    = filter.Multiplier
    };

    VideoColor                          D3D11VPBackgroundColor;
    ID3D11VideoDevice1                  vd1;
    ID3D11VideoProcessor                vp;
    ID3D11VideoContext                  vc;
    ID3D11VideoProcessorEnumerator      vpe;
    ID3D11VideoProcessorInputView       vpiv;
    ID3D11VideoProcessorOutputView      vpov;

    VideoProcessorStream[]              vpsa    = new VideoProcessorStream[] { new VideoProcessorStream() { Enable = true } };
    VideoProcessorContentDescription    vpcd    = new()
        {
            Usage = VideoUsage.PlaybackNormal,
            InputFrameFormat = VideoFrameFormat.InterlacedTopFieldFirst,

            InputFrameRate  = new Rational(1, 1),
            OutputFrameRate = new Rational(1, 1),
        };
    VideoProcessorOutputViewDescription vpovd   = new() { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
    VideoProcessorInputViewDescription  vpivd   = new()
        {
            FourCC          = 0,
            ViewDimension   = VideoProcessorInputViewDimension.Texture2D,
            Texture2D       = new Texture2DVideoProcessorInputView() { MipSlice = 0, ArraySlice = 0 }
        };
    VideoProcessorColorSpace            inputColorSpace;
    VideoProcessorColorSpace            outputColorSpace;

    AVDynamicHDRPlus*                   hdrPlusData = null;
    AVContentLightMetadata              lightData   = new();
    AVMasteringDisplayMetadata          displayData = new();

    uint actualRotation;
    bool actualHFlip, actualVFlip;
    bool configLoadedChecked;

    void InitializeVideoProcessor()
    {
        lock (VideoProcessorsCapsCache)
            try
            {
                vpcd.InputWidth = 1;
                vpcd.InputHeight= 1;
                vpcd.OutputWidth = vpcd.InputWidth;
                vpcd.OutputHeight= vpcd.InputHeight;

                outputColorSpace = new VideoProcessorColorSpace()
                {
                    Usage           = 0,
                    RGB_Range       = 0,
                    YCbCr_Matrix    = 1,
                    YCbCr_xvYCC     = 0,
                    Nominal_Range   = 2
                };

                if (VideoProcessorsCapsCache.ContainsKey(Device.Tag.ToString()))
                {
                    if (VideoProcessorsCapsCache[Device.Tag.ToString()].Failed)
                    {
                        InitializeFilters();
                        return;
                    }

                    vd1 = Device.QueryInterface<ID3D11VideoDevice1>();
                    vc  = context.QueryInterface<ID3D11VideoContext1>();

                    vd1.CreateVideoProcessorEnumerator(ref vpcd, out vpe);

                    if (vpe == null)
                    {
                        VPFailed();
                        return;
                    }

                    // if (!VideoProcessorsCapsCache[Device.Tag.ToString()].TypeIndex != -1)
                    vd1.CreateVideoProcessor(vpe, (uint)VideoProcessorsCapsCache[Device.Tag.ToString()].TypeIndex, out vp);
                    InitializeFilters();

                    return;
                }

                VideoProcessorCapsCache cache = new();
                VideoProcessorsCapsCache.Add(Device.Tag.ToString(), cache);

                vd1 = Device.QueryInterface<ID3D11VideoDevice1>();
                vc  = context.QueryInterface<ID3D11VideoContext>();

                vd1.CreateVideoProcessorEnumerator(ref vpcd, out vpe);

                if (vpe == null || Device.FeatureLevel < Vortice.Direct3D.FeatureLevel.Level_10_0)
                {
                    VPFailed();
                    return;
                }

                var vpe1 = vpe.QueryInterface<ID3D11VideoProcessorEnumerator1>();
                bool supportHLG = vpe1.CheckVideoProcessorFormatConversion(Format.P010, ColorSpaceType.YcbcrStudioGhlgTopLeftP2020, Format.B8G8R8A8_UNorm, ColorSpaceType.RgbFullG22NoneP709);
                bool supportHDR10Limited = vpe1.CheckVideoProcessorFormatConversion(Format.P010, ColorSpaceType.YcbcrStudioG2084TopLeftP2020, Format.B8G8R8A8_UNorm, ColorSpaceType.RgbStudioG2084NoneP2020);

                var vpCaps = vpe.VideoProcessorCaps;
                string dump = "";

                if (CanDebug)
                {
                    dump += $"=====================================================\r\n";
                    dump += $"MaxInputStreams           {vpCaps.MaxInputStreams}\r\n";
                    dump += $"MaxStreamStates           {vpCaps.MaxStreamStates}\r\n";
                    dump += $"HDR10 Limited             {(supportHDR10Limited ? "yes" : "no")}\r\n";
                    dump += $"HLG                       {(supportHLG ? "yes" : "no")}\r\n";

                    dump += $"\n[Video Processor Device Caps]\r\n";
                    foreach (VideoProcessorDeviceCaps cap in Enum.GetValues(typeof(VideoProcessorDeviceCaps)))
                        dump += $"{cap,-25} {((vpCaps.DeviceCaps & cap) != 0 ? "yes" : "no")}\r\n";

                    dump += $"\n[Video Processor Feature Caps]\r\n";
                    foreach (VideoProcessorFeatureCaps cap in Enum.GetValues(typeof(VideoProcessorFeatureCaps)))
                        dump += $"{cap,-25} {((vpCaps.FeatureCaps & cap) != 0 ? "yes" : "no")}\r\n";

                    dump += $"\n[Video Processor Stereo Caps]\r\n";
                    foreach (VideoProcessorStereoCaps cap in Enum.GetValues(typeof(VideoProcessorStereoCaps)))
                        dump += $"{cap,-25} {((vpCaps.StereoCaps & cap) != 0 ? "yes" : "no")}\r\n";

                    dump += $"\n[Video Processor Input Format Caps]\r\n";
                    foreach (VideoProcessorFormatCaps cap in Enum.GetValues(typeof(VideoProcessorFormatCaps)))
                        dump += $"{cap,-25} {((vpCaps.InputFormatCaps & cap) != 0 ? "yes" : "no")}\r\n";

                    dump += $"\n[Video Processor Filter Caps]\r\n";
                }

                foreach (VideoProcessorFilterCaps filter in Enum.GetValues(typeof(VideoProcessorFilterCaps)))
                    if ((vpCaps.FilterCaps & filter) != 0)
                    {
                        vpe1.GetVideoProcessorFilterRange(ConvertFromVideoProcessorFilterCaps(filter), out var range);
                        if (CanDebug) dump += $"{filter,-25} [{range.Minimum,6} - {range.Maximum,4}] | x{range.Multiplier,4} | *{range.Default}\r\n";
                        var vf = ConvertFromVideoProcessorFilterRange(range);
                        vf.Filter = (VideoFilters)filter;
                        cache.Filters.Add((VideoFilters)filter, vf);
                    }
                    else if (CanDebug)
                        dump += $"{filter,-25} no\r\n";

                if (CanDebug)
                {
                    dump += $"\n[Video Processor Input Format Caps]\r\n";
                    foreach (VideoProcessorAutoStreamCaps cap in Enum.GetValues(typeof(VideoProcessorAutoStreamCaps)))
                        dump += $"{cap,-25} {((vpCaps.AutoStreamCaps & cap) != 0 ? "yes" : "no")}\r\n";
                }

                uint typeIndex = 0;
                VideoProcessorRateConversionCaps rcCap = new();
                for (uint i = 0; i < vpCaps.RateConversionCapsCount; i++)
                {
                    vpe.GetVideoProcessorRateConversionCaps(i, out rcCap);
                    VideoProcessorProcessorCaps pCaps = (VideoProcessorProcessorCaps) rcCap.ProcessorCaps;

                    if (CanDebug)
                    {
                        dump += $"\n[Video Processor Rate Conversion Caps #{i}]\r\n";

                        dump += $"\n\t[Video Processor Rate Conversion Caps]\r\n";
                        var fields = typeof(VideoProcessorRateConversionCaps).GetFields();
                        foreach (var field in fields)
                            dump += $"\t{field.Name,-35} {field.GetValue(rcCap)}\r\n";

                        dump += $"\n\t[Video Processor Processor Caps]\r\n";
                        foreach (VideoProcessorProcessorCaps cap in Enum.GetValues(typeof(VideoProcessorProcessorCaps)))
                            dump += $"\t{cap,-35} {(((VideoProcessorProcessorCaps)rcCap.ProcessorCaps & cap) != 0 ? "yes" : "no")}\r\n";
                    }

                    typeIndex = i;

                    if (((VideoProcessorProcessorCaps)rcCap.ProcessorCaps & VideoProcessorProcessorCaps.DeinterlaceBob) != 0)
                        break; // TBR: When we add past/future frames support
                }
                vpe1.Dispose();

                if (CanDebug) Log.Debug($"D3D11 Video Processor\r\n{dump}");

                cache.TypeIndex = (int)typeIndex;
                cache.HLG = supportHLG;
                cache.HDR10Limited = supportHDR10Limited;
                cache.VideoProcessorCaps = vpCaps;
                cache.VideoProcessorRateConversionCaps = rcCap;

                //if (typeIndex != -1)
                vd1.CreateVideoProcessor(vpe, (uint)typeIndex, out vp);
                if (vp == null)
                {
                    VPFailed();
                    return;
                }

                cache.Failed = false;
                Log.Info($"D3D11 Video Processor Initialized (Rate Caps #{typeIndex})");

            } catch { DisposeVideoProcessor(); Log.Error($"D3D11 Video Processor Initialization Failed"); }

        InitializeFilters();
    }
    void VPFailed()
    {
        Log.Error($"D3D11 Video Processor Initialization Failed");

        if (!VideoProcessorsCapsCache.ContainsKey(Device.Tag.ToString()))
            VideoProcessorsCapsCache.Add(Device.Tag.ToString(), new VideoProcessorCapsCache());
        VideoProcessorsCapsCache[Device.Tag.ToString()].Failed = true;

        VideoProcessorsCapsCache[Device.Tag.ToString()].Filters.Add(VideoFilters.Brightness, new VideoFilter()  {  Filter = VideoFilters.Brightness });
        VideoProcessorsCapsCache[Device.Tag.ToString()].Filters.Add(VideoFilters.Contrast, new VideoFilter()    {  Filter = VideoFilters.Contrast });

        DisposeVideoProcessor();
        InitializeFilters();
    }
    void DisposeVideoProcessor()
    {
        vpiv?.Dispose();
        vpov?.Dispose();
        vp?.  Dispose();
        vpe?. Dispose();
        vc?.  Dispose();
        vd1?. Dispose();

        vc = null;
    }
    void InitializeFilters()
    {
        Filters = VideoProcessorsCapsCache[Device.Tag.ToString()].Filters;

        // Add FLVP filters if D3D11VP does not support them
        if (!Filters.ContainsKey(VideoFilters.Brightness))
            Filters.Add(VideoFilters.Brightness, new VideoFilter(VideoFilters.Brightness));

        if (!Filters.ContainsKey(VideoFilters.Contrast))
            Filters.Add(VideoFilters.Contrast, new VideoFilter(VideoFilters.Contrast));

        foreach(var filter in Filters.Values)
        {
            if (!Config.Video.Filters.ContainsKey(filter.Filter))
                continue;

            var cfgFilter = Config.Video.Filters[filter.Filter];
            cfgFilter.Available = true;
            cfgFilter.renderer = this;

            if (!configLoadedChecked && !Config.Loaded)
            {
                cfgFilter.Minimum       = filter.Minimum;
                cfgFilter.Maximum       = filter.Maximum;
                cfgFilter.DefaultValue  = filter.Value;
                cfgFilter.Value         = filter.Value;
                cfgFilter.Step          = filter.Step;
            }

            UpdateFilterValue(cfgFilter);
        }

        configLoadedChecked = true;
        UpdateBackgroundColor();

        if (vc != null)
        {
            vc.VideoProcessorSetStreamAutoProcessingMode(vp, 0, false);
            vc.VideoProcessorSetStreamFrameFormat(vp, 0, !Config.Video.Deinterlace ? VideoFrameFormat.Progressive : (Config.Video.DeinterlaceBottomFirst ? VideoFrameFormat.InterlacedBottomFieldFirst : VideoFrameFormat.InterlacedTopFieldFirst));
        }

        // Reset FLVP filters to defaults (can be different from D3D11VP filters scaling)
        if (videoProcessor == VideoProcessors.Flyleaf)
        {
            Config.Video.Filters[VideoFilters.Brightness].Value = Config.Video.Filters[VideoFilters.Brightness].Minimum + ((Config.Video.Filters[VideoFilters.Brightness].Maximum - Config.Video.Filters[VideoFilters.Brightness].Minimum) / 2);
            Config.Video.Filters[VideoFilters.Contrast].Value   = Config.Video.Filters[VideoFilters.Contrast].Minimum + ((Config.Video.Filters[VideoFilters.Contrast].Maximum - Config.Video.Filters[VideoFilters.Contrast].Minimum) / 2);
        }
    }

    internal void UpdateBackgroundColor()
    {
        D3D11VPBackgroundColor.Rgba.R = Scale(Config.Video.BackgroundColor.R, 0, 255, 0, 100) / 100.0f;
        D3D11VPBackgroundColor.Rgba.G = Scale(Config.Video.BackgroundColor.G, 0, 255, 0, 100) / 100.0f;
        D3D11VPBackgroundColor.Rgba.B = Scale(Config.Video.BackgroundColor.B, 0, 255, 0, 100) / 100.0f;

        vc?.VideoProcessorSetOutputBackgroundColor(vp, false, D3D11VPBackgroundColor);

        Present();
    }
    internal void UpdateDeinterlace()
    {
        lock (lockDevice)
        {
            if (Disposed)
                return;

            vc?.VideoProcessorSetStreamFrameFormat(vp, 0, !Config.Video.Deinterlace ? VideoFrameFormat.Progressive : (Config.Video.DeinterlaceBottomFirst ? VideoFrameFormat.InterlacedBottomFieldFirst : VideoFrameFormat.InterlacedTopFieldFirst));

            if (Config.Video.VideoProcessor != VideoProcessors.Auto)
                return;

            if (parent != null)
                return;

            ConfigPlanes();
            Present();
        }
    }
    internal void UpdateFilterValue(VideoFilter filter)
    {
        // D3D11VP
        if (Filters.ContainsKey(filter.Filter) && vc != null)
        {
            int scaledValue = (int) Scale(filter.Value, filter.Minimum, filter.Maximum, Filters[filter.Filter].Minimum, Filters[filter.Filter].Maximum);
            vc.VideoProcessorSetStreamFilter(vp, 0, ConvertFromVideoProcessorFilterCaps((VideoProcessorFilterCaps)filter.Filter), true, scaledValue);
        }

        if (parent != null)
            return;

        // FLVP
        switch (filter.Filter)
        {
            case VideoFilters.Brightness:
                int scaledValue = (int) Scale(filter.Value, filter.Minimum, filter.Maximum, 0, 100);
                psBufferData.brightness = scaledValue / 100.0f;
                context.UpdateSubresource(psBufferData, psBuffer);

                break;

            case VideoFilters.Contrast:
                scaledValue = (int) Scale(filter.Value, filter.Minimum, filter.Maximum, 0, 100);
                psBufferData.contrast = scaledValue / 100.0f;
                context.UpdateSubresource(psBufferData, psBuffer);

                break;

            default:
                break;
        }

        Present();
    }
    internal void UpdateHDRtoSDR(bool updateResource = true)
    {
        if(parent != null)
            return;

        float lum1 = 400;

        if (hdrPlusData != null)
        {
            lum1 = (float) (av_q2d(hdrPlusData->@params[0].average_maxrgb) * 100000.0);

            // this is not accurate more research required
            if (lum1 < 100)
                lum1 *= 10;
            lum1 = Math.Max(lum1, 400);
        }
        else if (Config.Video.HDRtoSDRMethod != HDRtoSDRMethod.Reinhard)
        {
            float lum2 = lum1;
            float lum3 = lum1;

            double lum = displayData.has_luminance != 0 ? av_q2d(displayData.max_luminance) : 400;

            if (lightData.MaxCLL > 0)
            {
                if (lightData.MaxCLL >= lum)
                {
                    lum1 = (float)lum;
                    lum2 = lightData.MaxCLL;
                }
                else
                {
                    lum1 = lightData.MaxCLL;
                    lum2 = (float)lum;
                }
                lum3 = lightData.MaxFALL;
                lum1 = (lum1 * 0.5f) + (lum2 * 0.2f) + (lum3 * 0.3f);
            }
            else
            {
                lum1 = (float)lum;
            }
        }
        else
        {
            if (lightData.MaxCLL > 0)
                lum1 = lightData.MaxCLL;
            else if (displayData.has_luminance != 0)
                lum1 = (float)av_q2d(displayData.max_luminance);
        }

        psBufferData.hdrmethod = Config.Video.HDRtoSDRMethod;

        if (psBufferData.hdrmethod == HDRtoSDRMethod.Hable)
        {
            psBufferData.g_luminance = lum1 > 1 ? lum1 : 400.0f;
            psBufferData.g_toneP1 = 10000.0f / psBufferData.g_luminance * (2.0f / Config.Video.HDRtoSDRTone);
            psBufferData.g_toneP2 = psBufferData.g_luminance / (100.0f * Config.Video.HDRtoSDRTone);
        }
        else if (psBufferData.hdrmethod == HDRtoSDRMethod.Reinhard)
        {
            psBufferData.g_toneP1 = lum1 > 0 ? (float)(Math.Log10(100) / Math.Log10(lum1)) : 0.72f;
            if (psBufferData.g_toneP1 < 0.1f || psBufferData.g_toneP1 > 5.0f)
                psBufferData.g_toneP1 = 0.72f;

            psBufferData.g_toneP1 *= Config.Video.HDRtoSDRTone;
        }
        else if (psBufferData.hdrmethod == HDRtoSDRMethod.Aces)
        {
            psBufferData.g_luminance = lum1 > 1 ? lum1 : 400.0f;
            psBufferData.g_toneP1 = Config.Video.HDRtoSDRTone;
        }

        if (updateResource)
        {
            context.UpdateSubresource(psBufferData, psBuffer);
            if (!VideoDecoder.IsRunning)
                Present();
        }
    }
    void UpdateRotation(uint angle, bool refresh = true)
    {
        _RotationAngle = angle;

        uint newRotation = _RotationAngle;

        if (VideoStream != null)
            newRotation += (uint)VideoStream.Rotation;

        if (rotationLinesize)
            newRotation += 180;

        newRotation %= 360;

        if (Disposed || (actualRotation == newRotation && actualHFlip == _HFlip && actualVFlip == _VFlip))
            return;

        bool hvFlipChanged = (actualHFlip || actualVFlip) != (_HFlip || _VFlip);

        actualRotation  = newRotation;
        actualHFlip     = _HFlip;
        actualVFlip     = _VFlip;

        if (actualRotation < 45 || actualRotation == 360)
            _d3d11vpRotation = VideoProcessorRotation.Identity;
        else if (actualRotation < 135)
            _d3d11vpRotation = VideoProcessorRotation.Rotation90;
        else if (actualRotation < 225)
            _d3d11vpRotation = VideoProcessorRotation.Rotation180;
        else if (actualRotation < 360)
            _d3d11vpRotation = VideoProcessorRotation.Rotation270;

        vsBufferData.mat  = Matrix4x4.CreateFromYawPitchRoll(0.0f, 0.0f, (float) (Math.PI / 180 * actualRotation));

        if (_HFlip || _VFlip)
        {
            vsBufferData.mat *= Matrix4x4.CreateScale(_HFlip ? -1 : 1, _VFlip ? -1 : 1, 1);
            if (hvFlipChanged)
            {
                // Renders both sides required for H-V Flip - TBR: consider for performance changing the vertex buffer / input layout instead?
                rasterizerState?.Dispose();
                rasterizerState = Device.CreateRasterizerState(new(CullMode.None, FillMode.Solid));
                context.RSSetState(rasterizerState);
            }
        }
        else if (hvFlipChanged)
        {
            // Removes back rendering for better performance
            rasterizerState?.Dispose();
            rasterizerState = Device.CreateRasterizerState(new(CullMode.Back, FillMode.Solid));
            context.RSSetState(rasterizerState);
        }

        if (parent == null)
            context.UpdateSubresource(vsBufferData, vsBuffer);

        if (child != null)
        {
            child.actualRotation    = actualRotation;
            child._d3d11vpRotation  = _d3d11vpRotation;
            child._RotationAngle    = _RotationAngle;
            child.rotationLinesize  = rotationLinesize;
            child.SetViewport();
        }

        vc?.VideoProcessorSetStreamRotation(vp, 0, true, _d3d11vpRotation);

        if (refresh)
            SetViewport();
    }
    internal void UpdateVideoProcessor()
    {
        if(parent != null)
            return;

        if (Config.Video.VideoProcessor == videoProcessor || (Config.Video.VideoProcessor == VideoProcessors.D3D11 && D3D11VPFailed))
            return;

        ConfigPlanes();
        Present();
    }
}

public class VideoFilter : NotifyPropertyChanged
{
    internal Renderer renderer;

    [JsonIgnore]
    public bool         Available   { get => _Available;    set => SetUI(ref _Available, value); }
    bool _Available;

    public VideoFilters Filter      { get => _Filter;       set => SetUI(ref _Filter, value); }
    VideoFilters _Filter = VideoFilters.Brightness;

    public int          Minimum     { get => _Minimum;      set => SetUI(ref _Minimum, value); }
    int _Minimum = 0;

    public int          Maximum     { get => _Maximum;      set => SetUI(ref _Maximum, value); }
    int _Maximum = 100;

    public float        Step        { get => _Step;         set => SetUI(ref _Step, value); }
    float _Step = 1;

    public int          DefaultValue
    {
        get;
        set
        {
            if (SetUI(ref field, value))
            {
                SetDefaultValue.OnCanExecuteChanged();
            }
        }
    } = 50;

    public int          Value
    {
        get;
        set
        {
            int v = value;
            v = Math.Min(v, Maximum);
            v = Math.Max(v, Minimum);

            if (Set(ref field, v))
            {
                renderer?.UpdateFilterValue(this);
                SetDefaultValue.OnCanExecuteChanged();
            }
        }
    } = 50;

    public RelayCommand SetDefaultValue => field ??= new(_ =>
    {
        Value = DefaultValue;
    }, _ => Value != DefaultValue);

    //internal void SetValue(int value) => SetUI(ref _Value, value, true, nameof(Value));

    public VideoFilter() { }
    public VideoFilter(VideoFilters filter, Player player = null)
        => Filter = filter;
}

public class VideoProcessorCapsCache
{
    public bool Failed = true;
    public int  TypeIndex = -1;
    public bool HLG;
    public bool HDR10Limited;
    public VideoProcessorCaps               VideoProcessorCaps;
    public VideoProcessorRateConversionCaps VideoProcessorRateConversionCaps;

    public Dictionary<VideoFilters, VideoFilter> Filters { get; set; } = new();
}
