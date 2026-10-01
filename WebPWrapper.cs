/////////////////////////////////////////////////////////////////////////////////////////////////////////////
/// Wrapper for WebP format in C#. (MIT) Jose M. Piñeiro
/// x86-only 精简版
/////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.WebPWrapper
{
    public sealed class WebP : IDisposable
    {
        private const int WEBP_MAX_DIMENSION = 16383;
        private UnsafeNativeMethods.WebPMemoryWrite _myWriterDelegate;

        #region | Public Decode Functions |

        public Bitmap Load(string pathFileName)
        {
            try
            {
                byte[] rawWebP = File.ReadAllBytes(pathFileName);
                return Decode(rawWebP);
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.Load"); }
        }

        public Bitmap Decode(byte[] rawWebP)
        {
            Bitmap bmp = null;
            BitmapData bmpData = null;
            GCHandle pinnedWebP = GCHandle.Alloc(rawWebP, GCHandleType.Pinned);

            try
            {
                GetInfo(rawWebP, out int imgWidth, out int imgHeight,
                        out bool hasAlpha, out bool hasAnimation, out string format);

                if (hasAlpha)
                    bmp = new Bitmap(imgWidth, imgHeight, PixelFormat.Format32bppArgb);
                else
                    bmp = new Bitmap(imgWidth, imgHeight, PixelFormat.Format24bppRgb);
                bmpData = bmp.LockBits(new Rectangle(0, 0, imgWidth, imgHeight),
                    ImageLockMode.WriteOnly, bmp.PixelFormat);

                int outputSize = bmpData.Stride * imgHeight;
                IntPtr ptrData = pinnedWebP.AddrOfPinnedObject();
                if (bmp.PixelFormat == PixelFormat.Format24bppRgb)
                    UnsafeNativeMethods.WebPDecodeBGRInto(ptrData, rawWebP.Length,
                        bmpData.Scan0, outputSize, bmpData.Stride);
                else
                    UnsafeNativeMethods.WebPDecodeBGRAInto(ptrData, rawWebP.Length,
                        bmpData.Scan0, outputSize, bmpData.Stride);

                return bmp;
            }
            catch (Exception) { throw; }
            finally
            {
                if (bmpData != null)
                    bmp.UnlockBits(bmpData);
                if (pinnedWebP.IsAllocated)
                    pinnedWebP.Free();
            }
        }

        public Bitmap Decode(byte[] rawWebP, WebPDecoderOptions options,
            PixelFormat pixelFormat = PixelFormat.DontCare)
        {
            GCHandle pinnedWebP = GCHandle.Alloc(rawWebP, GCHandleType.Pinned);
            Bitmap bmp = null;
            BitmapData bmpData = null;
            VP8StatusCode result;

            try
            {
                WebPDecoderConfig config = new WebPDecoderConfig();
                if (UnsafeNativeMethods.WebPInitDecoderConfig(ref config) == 0)
                    throw new Exception("WebPInitDecoderConfig failed. Wrong version?");

                IntPtr ptrRawWebP = pinnedWebP.AddrOfPinnedObject();
                int height, width;

                if (options.use_scaling == 0)
                {
                    result = UnsafeNativeMethods.WebPGetFeatures(
                        ptrRawWebP, rawWebP.Length, ref config.input);
                    if (result != VP8StatusCode.VP8_STATUS_OK)
                        throw new Exception("Failed WebPGetFeatures with error " + result);

                    if (options.use_cropping == 1)
                    {
                        if (options.crop_left + options.crop_width > config.input.Width ||
                            options.crop_top + options.crop_height > config.input.Height)
                            throw new Exception("Crop options exceeded WebP image dimensions");
                        width = options.crop_width;
                        height = options.crop_height;
                    }
                    else
                    {
                        width = config.input.Width;
                        height = config.input.Height;
                    }
                }
                else
                {
                    width = options.scaled_width;
                    height = options.scaled_height;
                }

                config.options.bypass_filtering = options.bypass_filtering;
                config.options.no_fancy_upsampling = options.no_fancy_upsampling;
                config.options.use_cropping = options.use_cropping;
                config.options.crop_left = options.crop_left;
                config.options.crop_top = options.crop_top;
                config.options.crop_width = options.crop_width;
                config.options.crop_height = options.crop_height;
                config.options.use_scaling = options.use_scaling;
                config.options.scaled_width = options.scaled_width;
                config.options.scaled_height = options.scaled_height;
                config.options.use_threads = options.use_threads;
                config.options.dithering_strength = options.dithering_strength;
                config.options.flip = options.flip;
                config.options.alpha_dithering_strength = options.alpha_dithering_strength;

                if (config.input.Has_alpha == 1)
                {
                    config.output.colorspace = WEBP_CSP_MODE.MODE_bgrA;
                    bmp = new Bitmap(config.input.Width, config.input.Height,
                        PixelFormat.Format32bppArgb);
                }
                else
                {
                    config.output.colorspace = WEBP_CSP_MODE.MODE_BGR;
                    bmp = new Bitmap(config.input.Width, config.input.Height,
                        PixelFormat.Format24bppRgb);
                }
                bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.WriteOnly, bmp.PixelFormat);

                config.output.u.RGBA.rgba = bmpData.Scan0;
                config.output.u.RGBA.stride = bmpData.Stride;
                config.output.u.RGBA.size = (UIntPtr)(bmp.Height * bmpData.Stride);
                config.output.height = bmp.Height;
                config.output.width = bmp.Width;
                config.output.is_external_memory = 1;

                result = UnsafeNativeMethods.WebPDecode(ptrRawWebP, rawWebP.Length, ref config);
                if (result != VP8StatusCode.VP8_STATUS_OK)
                    throw new Exception("Failed WebPDecode with error " + result);

                UnsafeNativeMethods.WebPFreeDecBuffer(ref config.output);
                return bmp;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.Decode"); }
            finally
            {
                if (bmpData != null)
                    bmp.UnlockBits(bmpData);
                if (pinnedWebP.IsAllocated)
                    pinnedWebP.Free();
            }
        }

        public Bitmap GetThumbnailFast(byte[] rawWebP, int width, int height)
        {
            GCHandle pinnedWebP = GCHandle.Alloc(rawWebP, GCHandleType.Pinned);
            Bitmap bmp = null;
            BitmapData bmpData = null;

            try
            {
                WebPDecoderConfig config = new WebPDecoderConfig();
                if (UnsafeNativeMethods.WebPInitDecoderConfig(ref config) == 0)
                    throw new Exception("WebPInitDecoderConfig failed. Wrong version?");

                config.options.bypass_filtering = 1;
                config.options.no_fancy_upsampling = 1;
                config.options.use_threads = 1;
                config.options.use_scaling = 1;
                config.options.scaled_width = width;
                config.options.scaled_height = height;

                bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, bmp.PixelFormat);

                config.output.colorspace = WEBP_CSP_MODE.MODE_BGR;
                config.output.u.RGBA.rgba = bmpData.Scan0;
                config.output.u.RGBA.stride = bmpData.Stride;
                config.output.u.RGBA.size = (UIntPtr)(height * bmpData.Stride);
                config.output.height = height;
                config.output.width = width;
                config.output.is_external_memory = 1;

                IntPtr ptrRawWebP = pinnedWebP.AddrOfPinnedObject();
                VP8StatusCode result = UnsafeNativeMethods.WebPDecode(
                    ptrRawWebP, rawWebP.Length, ref config);
                if (result != VP8StatusCode.VP8_STATUS_OK)
                    throw new Exception("Failed WebPDecode with error " + result);

                UnsafeNativeMethods.WebPFreeDecBuffer(ref config.output);
                return bmp;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.Thumbnail"); }
            finally
            {
                if (bmpData != null)
                    bmp.UnlockBits(bmpData);
                if (pinnedWebP.IsAllocated)
                    pinnedWebP.Free();
            }
        }

        public Bitmap GetThumbnailQuality(byte[] rawWebP, int width, int height)
        {
            GCHandle pinnedWebP = GCHandle.Alloc(rawWebP, GCHandleType.Pinned);
            Bitmap bmp = null;
            BitmapData bmpData = null;

            try
            {
                WebPDecoderConfig config = new WebPDecoderConfig();
                if (UnsafeNativeMethods.WebPInitDecoderConfig(ref config) == 0)
                    throw new Exception("WebPInitDecoderConfig failed. Wrong version?");

                IntPtr ptrRawWebP = pinnedWebP.AddrOfPinnedObject();
                VP8StatusCode result = UnsafeNativeMethods.WebPGetFeatures(
                    ptrRawWebP, rawWebP.Length, ref config.input);
                if (result != VP8StatusCode.VP8_STATUS_OK)
                    throw new Exception("Failed WebPGetFeatures with error " + result);

                config.options.bypass_filtering = 0;
                config.options.no_fancy_upsampling = 0;
                config.options.use_threads = 1;
                config.options.use_scaling = 1;
                config.options.scaled_width = width;
                config.options.scaled_height = height;

                if (config.input.Has_alpha == 1)
                {
                    config.output.colorspace = WEBP_CSP_MODE.MODE_bgrA;
                    bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                }
                else
                {
                    config.output.colorspace = WEBP_CSP_MODE.MODE_BGR;
                    bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                }
                bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, bmp.PixelFormat);

                config.output.u.RGBA.rgba = bmpData.Scan0;
                config.output.u.RGBA.stride = bmpData.Stride;
                config.output.u.RGBA.size = (UIntPtr)(height * bmpData.Stride);
                config.output.height = height;
                config.output.width = width;
                config.output.is_external_memory = 1;

                result = UnsafeNativeMethods.WebPDecode(ptrRawWebP, rawWebP.Length, ref config);
                if (result != VP8StatusCode.VP8_STATUS_OK)
                    throw new Exception("Failed WebPDecode with error " + result);

                UnsafeNativeMethods.WebPFreeDecBuffer(ref config.output);
                return bmp;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.Thumbnail"); }
            finally
            {
                if (bmpData != null)
                    bmp.UnlockBits(bmpData);
                if (pinnedWebP.IsAllocated)
                    pinnedWebP.Free();
            }
        }
        #endregion

        #region | Public Encode Functions |

        public void Save(Bitmap bmp, string pathFileName, int quality = 75)
        {
            byte[] rawWebP = EncodeLossy(bmp, quality);
            File.WriteAllBytes(pathFileName, rawWebP);
        }

        public byte[] EncodeLossy(Bitmap bmp, int quality = 75)
        {
            if (bmp.Width == 0 || bmp.Height == 0)
                throw new ArgumentException("Bitmap contains no data.", "bmp");
            if (bmp.Width > WEBP_MAX_DIMENSION || bmp.Height > WEBP_MAX_DIMENSION)
                throw new NotSupportedException("Bitmap's dimension is too large.");
            if (bmp.PixelFormat != PixelFormat.Format24bppRgb &&
                bmp.PixelFormat != PixelFormat.Format32bppArgb)
                throw new NotSupportedException("Only support Format24bppRgb and Format32bppArgb.");

            BitmapData bmpData = null;
            IntPtr unmanagedData = IntPtr.Zero;

            try
            {
                int size;
                bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly, bmp.PixelFormat);

                if (bmp.PixelFormat == PixelFormat.Format24bppRgb)
                    size = UnsafeNativeMethods.WebPEncodeBGR(bmpData.Scan0, bmp.Width,
                        bmp.Height, bmpData.Stride, quality, out unmanagedData);
                else
                    size = UnsafeNativeMethods.WebPEncodeBGRA(bmpData.Scan0, bmp.Width,
                        bmp.Height, bmpData.Stride, quality, out unmanagedData);

                if (size == 0) throw new Exception("Can´t encode WebP");

                byte[] rawWebP = new byte[size];
                Marshal.Copy(unmanagedData, rawWebP, 0, size);
                return rawWebP;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.EncodeLossy"); }
            finally
            {
                if (bmpData != null) bmp.UnlockBits(bmpData);
                if (unmanagedData != IntPtr.Zero) UnsafeNativeMethods.WebPFree(unmanagedData);
            }
        }

        public byte[] EncodeLossy(Bitmap bmp, int quality, int speed, bool info = false)
        {
            WebPConfig config = new WebPConfig();
            if (UnsafeNativeMethods.WebPConfigInit(ref config,
                WebPPreset.WEBP_PRESET_DEFAULT, 75) == 0)
                throw new Exception("Can´t configure preset");

            config.method = speed;
            if (config.method > 6) config.method = 6;
            config.quality = quality;
            config.autofilter = 1;
            config.pass = speed + 1;
            config.segments = 4;
            config.partitions = 3;
            config.thread_level = 1;
            config.alpha_quality = quality;
            config.alpha_filtering = 2;
            config.use_sharp_yuv = 1;

            if (UnsafeNativeMethods.WebPGetDecoderVersion() > 1082)
            {
                config.preprocessing = 4;
                config.use_sharp_yuv = 1;
            }
            else
            {
                config.preprocessing = 3;
            }

            return AdvancedEncode(bmp, config, info);
        }

        public byte[] EncodeLossless(Bitmap bmp)
        {
            if (bmp.Width == 0 || bmp.Height == 0)
                throw new ArgumentException("Bitmap contains no data.", "bmp");
            if (bmp.Width > WEBP_MAX_DIMENSION || bmp.Height > WEBP_MAX_DIMENSION)
                throw new NotSupportedException("Bitmap's dimension is too large.");
            if (bmp.PixelFormat != PixelFormat.Format24bppRgb &&
                bmp.PixelFormat != PixelFormat.Format32bppArgb)
                throw new NotSupportedException("Only support Format24bppRgb and Format32bppArgb.");

            BitmapData bmpData = null;
            IntPtr unmanagedData = IntPtr.Zero;
            try
            {
                bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly, bmp.PixelFormat);

                int size;
                if (bmp.PixelFormat == PixelFormat.Format24bppRgb)
                    size = UnsafeNativeMethods.WebPEncodeLosslessBGR(
                        bmpData.Scan0, bmp.Width, bmp.Height, bmpData.Stride, out unmanagedData);
                else
                    size = UnsafeNativeMethods.WebPEncodeLosslessBGRA(
                        bmpData.Scan0, bmp.Width, bmp.Height, bmpData.Stride, out unmanagedData);

                byte[] rawWebP = new byte[size];
                Marshal.Copy(unmanagedData, rawWebP, 0, size);
                return rawWebP;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.EncodeLossless"); }
            finally
            {
                if (bmpData != null) bmp.UnlockBits(bmpData);
                if (unmanagedData != IntPtr.Zero) UnsafeNativeMethods.WebPFree(unmanagedData);
            }
        }

        public byte[] EncodeLossless(Bitmap bmp, int speed)
        {
            WebPConfig config = new WebPConfig();
            if (UnsafeNativeMethods.WebPConfigInit(ref config,
                WebPPreset.WEBP_PRESET_DEFAULT, (speed + 1) * 10) == 0)
                throw new Exception("Can´t config preset");

            if (UnsafeNativeMethods.WebPGetDecoderVersion() > 1082)
            {
                if (UnsafeNativeMethods.WebPConfigLosslessPreset(ref config, speed) == 0)
                    throw new Exception("Can´t configure lossless preset");
            }
            else
            {
                config.lossless = 1;
                config.method = speed;
                if (config.method > 6) config.method = 6;
                config.quality = (speed + 1) * 10;
            }
            config.pass = speed + 1;
            config.thread_level = 1;
            config.alpha_filtering = 2;
            config.use_sharp_yuv = 1;
            config.exact = 0;

            return AdvancedEncode(bmp, config, false);
        }

        public byte[] EncodeNearLossless(Bitmap bmp, int quality, int speed = 9)
        {
            if (UnsafeNativeMethods.WebPGetDecoderVersion() <= 1082)
                throw new Exception("This DLL version not support EncodeNearLossless");

            WebPConfig config = new WebPConfig();
            if (UnsafeNativeMethods.WebPConfigInit(ref config,
                WebPPreset.WEBP_PRESET_DEFAULT, (speed + 1) * 10) == 0)
                throw new Exception("Can´t configure preset");
            if (UnsafeNativeMethods.WebPConfigLosslessPreset(ref config, speed) == 0)
                throw new Exception("Can´t configure lossless preset");

            config.pass = speed + 1;
            config.near_lossless = quality;
            config.thread_level = 1;
            config.alpha_filtering = 2;
            config.use_sharp_yuv = 1;
            config.exact = 0;

            return AdvancedEncode(bmp, config, false);
        }
        #endregion

        #region | Another Public Functions |

        public string GetVersion()
        {
            try
            {
                uint v = (uint)UnsafeNativeMethods.WebPGetDecoderVersion();
                var revision = v % 256;
                var minor = (v >> 8) % 256;
                var major = (v >> 16) % 256;
                return major + "." + minor + "." + revision;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.GetVersion"); }
        }

        public void GetInfo(byte[] rawWebP, out int width, out int height,
            out bool has_alpha, out bool has_animation, out string format)
        {
            GCHandle pinnedWebP = GCHandle.Alloc(rawWebP, GCHandleType.Pinned);
            try
            {
                IntPtr ptrRawWebP = pinnedWebP.AddrOfPinnedObject();
                WebPBitstreamFeatures features = new WebPBitstreamFeatures();
                VP8StatusCode result = UnsafeNativeMethods.WebPGetFeatures(
                    ptrRawWebP, rawWebP.Length, ref features);

                if (result != 0)
                    throw new Exception(result.ToString());

                width = features.Width;
                height = features.Height;
                has_alpha = features.Has_alpha == 1;
                has_animation = features.Has_animation == 1;
                switch (features.Format)
                {
                    case 1: format = "lossy"; break;
                    case 2: format = "lossless"; break;
                    default: format = "undefined"; break;
                }
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.GetInfo"); }
            finally
            {
                if (pinnedWebP.IsAllocated) pinnedWebP.Free();
            }
        }

        public float[] GetPictureDistortion(Bitmap source, Bitmap reference, int metric_type)
        {
            WebPPicture wpicSource = new WebPPicture();
            WebPPicture wpicReference = new WebPPicture();
            BitmapData sourceBmpData = null;
            BitmapData referenceBmpData = null;
            float[] result = new float[5];
            GCHandle pinnedResult = GCHandle.Alloc(result, GCHandleType.Pinned);

            try
            {
                if (source == null) throw new Exception("Source picture is void");
                if (reference == null) throw new Exception("Reference picture is void");
                if (metric_type > 2) throw new Exception("Bad metric_type.");

                sourceBmpData = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                    ImageLockMode.ReadOnly, source.PixelFormat);
                wpicSource = new WebPPicture();
                if (UnsafeNativeMethods.WebPPictureInitInternal(ref wpicSource) != 1)
                    throw new Exception("Can´t initialize WebPPictureInit");
                wpicSource.width = (int)source.Width;
                wpicSource.height = (int)source.Height;

                if (sourceBmpData.PixelFormat == PixelFormat.Format32bppArgb)
                {
                    wpicSource.use_argb = 1;
                    if (UnsafeNativeMethods.WebPPictureImportBGRA(ref wpicSource,
                        sourceBmpData.Scan0, sourceBmpData.Stride) != 1)
                        throw new Exception("Can´t allocate memory in WebPPictureImportBGRA");
                }
                else
                {
                    wpicSource.use_argb = 0;
                    if (UnsafeNativeMethods.WebPPictureImportBGR(ref wpicSource,
                        sourceBmpData.Scan0, sourceBmpData.Stride) != 1)
                        throw new Exception("Can´t allocate memory in WebPPictureImportBGR");
                }

                referenceBmpData = reference.LockBits(
                    new Rectangle(0, 0, reference.Width, reference.Height),
                    ImageLockMode.ReadOnly, reference.PixelFormat);
                wpicReference = new WebPPicture();
                if (UnsafeNativeMethods.WebPPictureInitInternal(ref wpicReference) != 1)
                    throw new Exception("Can´t initialize WebPPictureInit");
                wpicReference.width = (int)reference.Width;
                wpicReference.height = (int)reference.Height;
                wpicReference.use_argb = 1;

                if (sourceBmpData.PixelFormat == PixelFormat.Format32bppArgb)
                {
                    wpicSource.use_argb = 1;
                    if (UnsafeNativeMethods.WebPPictureImportBGRA(ref wpicReference,
                        referenceBmpData.Scan0, referenceBmpData.Stride) != 1)
                        throw new Exception("Can´t allocate memory in WebPPictureImportBGRA");
                }
                else
                {
                    wpicSource.use_argb = 0;
                    if (UnsafeNativeMethods.WebPPictureImportBGR(ref wpicReference,
                        referenceBmpData.Scan0, referenceBmpData.Stride) != 1)
                        throw new Exception("Can´t allocate memory in WebPPictureImportBGR");
                }

                IntPtr ptrResult = pinnedResult.AddrOfPinnedObject();
                if (UnsafeNativeMethods.WebPPictureDistortion(
                    ref wpicSource, ref wpicReference, metric_type, ptrResult) != 1)
                    throw new Exception("Can´t measure.");

                return result;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.GetPictureDistortion"); }
            finally
            {
                if (sourceBmpData != null) source.UnlockBits(sourceBmpData);
                if (referenceBmpData != null) reference.UnlockBits(referenceBmpData);
                if (wpicSource.argb != IntPtr.Zero)
                    UnsafeNativeMethods.WebPPictureFree(ref wpicSource);
                if (wpicReference.argb != IntPtr.Zero)
                    UnsafeNativeMethods.WebPPictureFree(ref wpicReference);
                if (pinnedResult.IsAllocated) pinnedResult.Free();
            }
        }
        #endregion

        #region | Private Methods |

        private byte[] AdvancedEncode(Bitmap bmp, WebPConfig config, bool info)
        {
            byte[] rawWebP = null;
            byte[] dataWebp = null;
            WebPPicture wpic = new WebPPicture();
            BitmapData bmpData = null;
            WebPAuxStats stats = new WebPAuxStats();
            IntPtr ptrStats = IntPtr.Zero;
            GCHandle pinnedArrayHandle = new GCHandle();
            int dataWebpSize;

            try
            {
                if (UnsafeNativeMethods.WebPValidateConfig(ref config) != 1)
                    throw new Exception("Bad configuration parameters");

                if (bmp.Width == 0 || bmp.Height == 0)
                    throw new ArgumentException("Bitmap contains no data.", "bmp");
                if (bmp.Width > WEBP_MAX_DIMENSION || bmp.Height > WEBP_MAX_DIMENSION)
                    throw new NotSupportedException("Bitmap's dimension is too large.");
                if (bmp.PixelFormat != PixelFormat.Format24bppRgb &&
                    bmp.PixelFormat != PixelFormat.Format32bppArgb)
                    throw new NotSupportedException("Only support Format24bppRgb and Format32bppArgb.");

                bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly, bmp.PixelFormat);
                if (UnsafeNativeMethods.WebPPictureInitInternal(ref wpic) != 1)
                    throw new Exception("Can´t initialize WebPPictureInit");
                wpic.width = (int)bmp.Width;
                wpic.height = (int)bmp.Height;
                wpic.use_argb = 1;

                if (bmp.PixelFormat == PixelFormat.Format32bppArgb)
                {
                    int result = UnsafeNativeMethods.WebPPictureImportBGRA(
                        ref wpic, bmpData.Scan0, bmpData.Stride);
                    if (result != 1)
                        throw new Exception("Can´t allocate memory in WebPPictureImportBGRA");
                    wpic.colorspace = (uint)WEBP_CSP_MODE.MODE_bgrA;
                    dataWebpSize = bmp.Width * bmp.Height * 32;
                    dataWebp = new byte[bmp.Width * bmp.Height * 32];
                }
                else
                {
                    int result = UnsafeNativeMethods.WebPPictureImportBGR(
                        ref wpic, bmpData.Scan0, bmpData.Stride);
                    if (result != 1)
                        throw new Exception("Can´t allocate memory in WebPPictureImportBGR");
                    dataWebpSize = bmp.Width * bmp.Height * 24;
                }

                if (info)
                {
                    stats = new WebPAuxStats();
                    ptrStats = Marshal.AllocHGlobal(Marshal.SizeOf(stats));
                    Marshal.StructureToPtr(stats, ptrStats, false);
                    wpic.stats = ptrStats;
                }

                if (dataWebpSize > 2147483591)
                    dataWebpSize = 2147483591;
                dataWebp = new byte[bmp.Width * bmp.Height * 32];
                pinnedArrayHandle = GCHandle.Alloc(dataWebp, GCHandleType.Pinned);
                IntPtr initPtr = pinnedArrayHandle.AddrOfPinnedObject();
                wpic.custom_ptr = initPtr;

                _myWriterDelegate = new UnsafeNativeMethods.WebPMemoryWrite(MyWriter);
                wpic.writer = Marshal.GetFunctionPointerForDelegate(_myWriterDelegate);

                if (UnsafeNativeMethods.WebPEncode(ref config, ref wpic) != 1)
                    throw new Exception("Encoding error: " +
                        ((WebPEncodingError)wpic.error_code).ToString());

                _myWriterDelegate = null;

                bmp.UnlockBits(bmpData);
                bmpData = null;

                int size = (int)((long)wpic.custom_ptr - (long)initPtr);
                rawWebP = new byte[size];
                Array.Copy(dataWebp, rawWebP, size);

                pinnedArrayHandle.Free();
                dataWebp = null;

                if (info)
                {
                    stats = (WebPAuxStats)Marshal.PtrToStructure(ptrStats, typeof(WebPAuxStats));
                    Debug.Print("Dimension: " + wpic.width + " x " + wpic.height);
                }

                return rawWebP;
            }
            catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.AdvancedEncode"); }
            finally
            {
                if (pinnedArrayHandle.IsAllocated) pinnedArrayHandle.Free();
                if (ptrStats != IntPtr.Zero) Marshal.FreeHGlobal(ptrStats);
                if (bmpData != null) bmp.UnlockBits(bmpData);
                if (wpic.argb != IntPtr.Zero) UnsafeNativeMethods.WebPPictureFree(ref wpic);
            }
        }

        private int MyWriter([InAttribute()] IntPtr data, UIntPtr data_size, ref WebPPicture picture)
        {
            UnsafeNativeMethods.CopyMemory(picture.custom_ptr, data, (uint)data_size);
            picture.custom_ptr = new IntPtr(picture.custom_ptr.ToInt64() + (int)data_size);
            return 1;
        }

        private delegate int MyWriterDelegate([InAttribute()] IntPtr data,
            UIntPtr data_size, ref WebPPicture picture);
        #endregion

        #region | Destruction |
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
        #endregion
    }

    #region | Import libwebp functions |

    [SuppressUnmanagedCodeSecurityAttribute]
    internal sealed partial class UnsafeNativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        internal static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        private static readonly int WEBP_DECODER_ABI_VERSION = 0x0208;

        // -------- Config --------
        internal static int WebPConfigInit(ref WebPConfig config, WebPPreset preset, float quality)
        {
            return WebPConfigInitInternal(ref config, preset, quality, WEBP_DECODER_ABI_VERSION);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPConfigInitInternal")]
        private static extern int WebPConfigInitInternal(ref WebPConfig config,
            WebPPreset preset, float quality, int WEBP_DECODER_ABI_VERSION);

        internal static int WebPConfigLosslessPreset(ref WebPConfig config, int level)
        {
            return WebPConfigLosslessPresetInternal(ref config, level);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPConfigLosslessPreset")]
        private static extern int WebPConfigLosslessPresetInternal(ref WebPConfig config, int level);

        internal static int WebPValidateConfig(ref WebPConfig config)
        {
            return WebPValidateConfigInternal(ref config);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPValidateConfig")]
        private static extern int WebPValidateConfigInternal(ref WebPConfig config);

        // -------- Picture --------
        internal static int WebPPictureInitInternal(ref WebPPicture wpic)
        {
            return WebPPictureInitInternalImpl(ref wpic, WEBP_DECODER_ABI_VERSION);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPPictureInitInternal")]
        private static extern int WebPPictureInitInternalImpl(ref WebPPicture wpic,
            int WEBP_DECODER_ABI_VERSION);

        internal static int WebPPictureImportBGR(ref WebPPicture wpic, IntPtr bgr, int stride)
        {
            return WebPPictureImportBGRImpl(ref wpic, bgr, stride);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPPictureImportBGR")]
        private static extern int WebPPictureImportBGRImpl(ref WebPPicture wpic,
            IntPtr bgr, int stride);

        internal static int WebPPictureImportBGRA(ref WebPPicture wpic, IntPtr bgra, int stride)
        {
            return WebPPictureImportBGRAImpl(ref wpic, bgra, stride);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPPictureImportBGRA")]
        private static extern int WebPPictureImportBGRAImpl(ref WebPPicture wpic,
            IntPtr bgra, int stride);

        internal static int WebPPictureImportBGRX(ref WebPPicture wpic, IntPtr bgr, int stride)
        {
            return WebPPictureImportBGRXImpl(ref wpic, bgr, stride);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPPictureImportBGRX")]
        private static extern int WebPPictureImportBGRXImpl(ref WebPPicture wpic,
            IntPtr bgr, int stride);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int WebPMemoryWrite([In()] IntPtr data, UIntPtr data_size,
            ref WebPPicture wpic);

        internal static int WebPEncode(ref WebPConfig config, ref WebPPicture picture)
        {
            return WebPEncodeImpl(ref config, ref picture);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPEncode")]
        private static extern int WebPEncodeImpl(ref WebPConfig config, ref WebPPicture picture);

        internal static void WebPPictureFree(ref WebPPicture picture)
        {
            WebPPictureFreeImpl(ref picture);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPPictureFree")]
        private static extern void WebPPictureFreeImpl(ref WebPPicture wpic);

        // -------- Decoder --------
        internal static int WebPGetInfo(IntPtr data, int data_size, out int width, out int height)
        {
            return WebPGetInfoImpl(data, (UIntPtr)data_size, out width, out height);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPGetInfo")]
        private static extern int WebPGetInfoImpl([InAttribute()] IntPtr data,
            UIntPtr data_size, out int width, out int height);

        internal static void WebPDecodeBGRInto(IntPtr data, int data_size,
            IntPtr output_buffer, int output_buffer_size, int output_stride)
        {
            if (WebPDecodeBGRIntoImpl(data, (UIntPtr)data_size, output_buffer,
                output_buffer_size, output_stride) == null)
                throw new InvalidOperationException("Can not decode WebP");
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPDecodeBGRInto")]
        private static extern IntPtr WebPDecodeBGRIntoImpl([InAttribute()] IntPtr data,
            UIntPtr data_size, IntPtr output_buffer, int output_buffer_size, int output_stride);

        internal static void WebPDecodeBGRAInto(IntPtr data, int data_size,
            IntPtr output_buffer, int output_buffer_size, int output_stride)
        {
            if (WebPDecodeBGRAIntoImpl(data, (UIntPtr)data_size, output_buffer,
                output_buffer_size, output_stride) == null)
                throw new InvalidOperationException("Can not decode WebP");
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPDecodeBGRAInto")]
        private static extern IntPtr WebPDecodeBGRAIntoImpl([InAttribute()] IntPtr data,
            UIntPtr data_size, IntPtr output_buffer, int output_buffer_size, int output_stride);

        internal static void WebPDecodeARGBInto(IntPtr data, int data_size,
            IntPtr output_buffer, int output_buffer_size, int output_stride)
        {
            if (WebPDecodeARGBIntoImpl(data, (UIntPtr)data_size, output_buffer,
                output_buffer_size, output_stride) == null)
                throw new InvalidOperationException("Can not decode WebP");
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPDecodeARGBInto")]
        private static extern IntPtr WebPDecodeARGBIntoImpl([InAttribute()] IntPtr data,
            UIntPtr data_size, IntPtr output_buffer, int output_buffer_size, int output_stride);

        internal static int WebPInitDecoderConfig(ref WebPDecoderConfig webPDecoderConfig)
        {
            return WebPInitDecoderConfigInternalImpl(ref webPDecoderConfig,
                WEBP_DECODER_ABI_VERSION);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPInitDecoderConfigInternal")]
        private static extern int WebPInitDecoderConfigInternalImpl(
            ref WebPDecoderConfig webPDecoderConfig, int WEBP_DECODER_ABI_VERSION);

        internal static VP8StatusCode WebPDecode(IntPtr data, int data_size,
            ref WebPDecoderConfig webPDecoderConfig)
        {
            return WebPDecodeImpl(data, (UIntPtr)data_size, ref webPDecoderConfig);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPDecode")]
        private static extern VP8StatusCode WebPDecodeImpl(IntPtr data, UIntPtr data_size,
            ref WebPDecoderConfig config);

        internal static void WebPFreeDecBuffer(ref WebPDecBuffer buffer)
        {
            WebPFreeDecBufferImpl(ref buffer);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPFreeDecBuffer")]
        private static extern void WebPFreeDecBufferImpl(ref WebPDecBuffer buffer);

        // -------- Features --------
        internal static VP8StatusCode WebPGetFeatures(IntPtr rawWebP, int data_size,
            ref WebPBitstreamFeatures features)
        {
            return WebPGetFeaturesInternalImpl(rawWebP, (UIntPtr)data_size,
                ref features, WEBP_DECODER_ABI_VERSION);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPGetFeaturesInternal")]
        private static extern VP8StatusCode WebPGetFeaturesInternalImpl(
            [InAttribute()] IntPtr rawWebP, UIntPtr data_size,
            ref WebPBitstreamFeatures features, int WEBP_DECODER_ABI_VERSION);

        // -------- Encode (Simple API) --------
        internal static int WebPEncodeBGR(IntPtr bgr, int width, int height, int stride,
            float quality_factor, out IntPtr output)
        {
            return WebPEncodeBGRImpl(bgr, width, height, stride, quality_factor, out output);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPEncodeBGR")]
        private static extern int WebPEncodeBGRImpl([InAttribute()] IntPtr bgr,
            int width, int height, int stride, float quality_factor, out IntPtr output);

        internal static int WebPEncodeBGRA(IntPtr bgra, int width, int height, int stride,
            float quality_factor, out IntPtr output)
        {
            return WebPEncodeBGRAImpl(bgra, width, height, stride, quality_factor, out output);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPEncodeBGRA")]
        private static extern int WebPEncodeBGRAImpl([InAttribute()] IntPtr bgra,
            int width, int height, int stride, float quality_factor, out IntPtr output);

        internal static int WebPEncodeLosslessBGR(IntPtr bgr, int width, int height,
            int stride, out IntPtr output)
        {
            return WebPEncodeLosslessBGRImpl(bgr, width, height, stride, out output);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPEncodeLosslessBGR")]
        private static extern int WebPEncodeLosslessBGRImpl([InAttribute()] IntPtr bgr,
            int width, int height, int stride, out IntPtr output);

        internal static int WebPEncodeLosslessBGRA(IntPtr bgra, int width, int height,
            int stride, out IntPtr output)
        {
            return WebPEncodeLosslessBGRAImpl(bgra, width, height, stride, out output);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPEncodeLosslessBGRA")]
        private static extern int WebPEncodeLosslessBGRAImpl([InAttribute()] IntPtr bgra,
            int width, int height, int stride, out IntPtr output);

        internal static void WebPFree(IntPtr p)
        {
            WebPFreeImpl(p);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPFree")]
        private static extern void WebPFreeImpl(IntPtr p);

        internal static int WebPGetDecoderVersion()
        {
            return WebPGetDecoderVersionImpl();
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPGetDecoderVersion")]
        private static extern int WebPGetDecoderVersionImpl();

        internal static int WebPPictureDistortion(ref WebPPicture srcPicture,
            ref WebPPicture refPicture, int metric_type, IntPtr pResult)
        {
            return WebPPictureDistortionImpl(ref srcPicture, ref refPicture, metric_type, pResult);
        }
        [DllImport("libwebp_x86.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "WebPPictureDistortion")]
        private static extern int WebPPictureDistortionImpl(ref WebPPicture srcPicture,
            ref WebPPicture refPicture, int metric_type, IntPtr pResult);
    }
    #endregion

    #region | Predefined |
    internal enum WebPPreset
    {
        WEBP_PRESET_DEFAULT = 0,
        WEBP_PRESET_PICTURE,
        WEBP_PRESET_PHOTO,
        WEBP_PRESET_DRAWING,
        WEBP_PRESET_ICON,
        WEBP_PRESET_TEXT
    };

    internal enum WebPEncodingError
    {
        VP8_ENC_OK = 0,
        VP8_ENC_ERROR_OUT_OF_MEMORY,
        VP8_ENC_ERROR_BITSTREAM_OUT_OF_MEMORY,
        VP8_ENC_ERROR_NULL_PARAMETER,
        VP8_ENC_ERROR_INVALID_CONFIGURATION,
        VP8_ENC_ERROR_BAD_DIMENSION,
        VP8_ENC_ERROR_PARTITION0_OVERFLOW,
        VP8_ENC_ERROR_PARTITION_OVERFLOW,
        VP8_ENC_ERROR_BAD_WRITE,
        VP8_ENC_ERROR_FILE_TOO_BIG,
        VP8_ENC_ERROR_USER_ABORT,
        VP8_ENC_ERROR_LAST,
    }

    internal enum VP8StatusCode
    {
        VP8_STATUS_OK = 0,
        VP8_STATUS_OUT_OF_MEMORY,
        VP8_STATUS_INVALID_PARAM,
        VP8_STATUS_BITSTREAM_ERROR,
        VP8_STATUS_UNSUPPORTED_FEATURE,
        VP8_STATUS_SUSPENDED,
        VP8_STATUS_USER_ABORT,
        VP8_STATUS_NOT_ENOUGH_DATA,
    }

    internal enum WebPImageHint
    {
        WEBP_HINT_DEFAULT = 0,
        WEBP_HINT_PICTURE,
        WEBP_HINT_PHOTO,
        WEBP_HINT_GRAPH,
        WEBP_HINT_LAST
    };

    internal enum WEBP_CSP_MODE
    {
        MODE_RGB = 0,
        MODE_RGBA = 1,
        MODE_BGR = 2,
        MODE_BGRA = 3,
        MODE_ARGB = 4,
        MODE_RGBA_4444 = 5,
        MODE_RGB_565 = 6,
        MODE_rgbA = 7,
        MODE_bgrA = 8,
        MODE_Argb = 9,
        MODE_rgbA_4444 = 10,
        MODE_YUV = 11,
        MODE_YUVA = 12,
        MODE_LAST = 13,
    }

    internal enum DecState
    {
        STATE_WEBP_HEADER,
        STATE_VP8_HEADER,
        STATE_VP8_PARTS0,
        STATE_VP8_DATA,
        STATE_VP8L_HEADER,
        STATE_VP8L_DATA,
        STATE_DONE,
        STATE_ERROR
    };
    #endregion

    #region | libwebp structs |
    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPBitstreamFeatures
    {
        public int Width;
        public int Height;
        public int Has_alpha;
        public int Has_animation;
        public int Format;
        [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 5, ArraySubType = UnmanagedType.U4)]
        private readonly uint[] pad;
    };

    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPConfig
    {
        public int lossless;
        public float quality;
        public int method;
        public WebPImageHint image_hint;
        public int target_size;
        public float target_PSNR;
        public int segments;
        public int sns_strength;
        public int filter_strength;
        public int filter_sharpness;
        public int filter_type;
        public int autofilter;
        public int alpha_compression;
        public int alpha_filtering;
        public int alpha_quality;
        public int pass;
        public int show_compressed;
        public int preprocessing;
        public int partitions;
        public int partition_limit;
        public int emulate_jpeg_size;
        public int thread_level;
        public int low_memory;
        public int near_lossless;
        public int exact;
        public int delta_palettization;
        public int use_sharp_yuv;
        private readonly int pad1;
        private readonly int pad2;
    };

    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPPicture
    {
        public int use_argb;
        public UInt32 colorspace;
        public int width;
        public int height;
        public IntPtr y;
        public IntPtr u;
        public IntPtr v;
        public int y_stride;
        public int uv_stride;
        public IntPtr a;
        public int a_stride;
        [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.U4)]
        private readonly uint[] pad1;
        public IntPtr argb;
        public int argb_stride;
        [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.U4)]
        private readonly uint[] pad2;
        public IntPtr writer;
        public IntPtr custom_ptr;
        public int extra_info_type;
        public IntPtr extra_info;
        public IntPtr stats;
        public UInt32 error_code;
        public IntPtr progress_hook;
        public IntPtr user_data;
        [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 13, ArraySubType = UnmanagedType.U4)]
        private readonly uint[] pad3;
        private readonly IntPtr memory_;
        private readonly IntPtr memory_argb_;
        [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.U4)]
        private readonly uint[] pad4;
    };

    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPAuxStats
    {
        public int coded_size;
        public float PSNRY;
        public float PSNRU;
        public float PSNRV;
        public float PSNRALL;
        public float PSNRAlpha;
        public int block_count_intra4;
        public int block_count_intra16;
        public int block_count_skipped;
        public int header_bytes;
        public int mode_partition_0;
        public int residual_bytes_DC_segments0;
        public int residual_bytes_AC_segments0;
        public int residual_bytes_uv_segments0;
        public int residual_bytes_DC_segments1;
        public int residual_bytes_AC_segments1;
        public int residual_bytes_uv_segments1;
        public int residual_bytes_DC_segments2;
        public int residual_bytes_AC_segments2;
        public int residual_bytes_uv_segments2;
        public int residual_bytes_DC_segments3;
        public int residual_bytes_AC_segments3;
        public int residual_bytes_uv_segments3;
        public int segment_size_segments0;
        public int segment_size_segments1;
        public int segment_size_segments2;
        public int segment_size_segments3;
        public int segment_quant_segments0;
        public int segment_quant_segments1;
        public int segment_quant_segments2;
        public int segment_quant_segments3;
        public int segment_level_segments0;
        public int segment_level_segments1;
        public int segment_level_segments2;
        public int segment_level_segments3;
        public int alpha_data_size;
        public int layer_data_size;
        public Int32 lossless_features;
        public int histogram_bits;
        public int transform_bits;
        public int cache_bits;
        public int palette_size;
        public int lossless_size;
        public int lossless_hdr_size;
        public int lossless_data_size;
        [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.U4)]
        private readonly uint[] pad;
    };

    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPDecoderConfig
    {
        public WebPBitstreamFeatures input;
        public WebPDecBuffer output;
        public WebPDecoderOptions options;
    }

    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPDecBuffer
    {
        public WEBP_CSP_MODE colorspace;
        public int width;
        public int height;
        public int is_external_memory;
        public RGBA_YUVA_Buffer u;
        private readonly UInt32 pad1;
        private readonly UInt32 pad2;
        private readonly UInt32 pad3;
        private readonly UInt32 pad4;
        public IntPtr private_memory;
    }

    [StructLayoutAttribute(LayoutKind.Explicit)]
    internal struct RGBA_YUVA_Buffer
    {
        [FieldOffsetAttribute(0)]
        public WebPRGBABuffer RGBA;
        [FieldOffsetAttribute(0)]
        public WebPYUVABuffer YUVA;
    }

    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPYUVABuffer
    {
        public IntPtr y;
        public IntPtr u;
        public IntPtr v;
        public IntPtr a;
        public int y_stride;
        public int u_stride;
        public int v_stride;
        public int a_stride;
        public UIntPtr y_size;
        public UIntPtr u_size;
        public UIntPtr v_size;
        public UIntPtr a_size;
    }

    [StructLayoutAttribute(LayoutKind.Sequential)]
    internal struct WebPRGBABuffer
    {
        public IntPtr rgba;
        public int stride;
        public UIntPtr size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WebPDecoderOptions
    {
        public int bypass_filtering;
        public int no_fancy_upsampling;
        public int use_cropping;
        public int crop_left;
        public int crop_top;
        public int crop_width;
        public int crop_height;
        public int use_scaling;
        public int scaled_width;
        public int scaled_height;
        public int use_threads;
        public int dithering_strength;
        public int flip;
        public int alpha_dithering_strength;
        private readonly UInt32 pad1;
        private readonly UInt32 pad2;
        private readonly UInt32 pad3;
        private readonly UInt32 pad4;
        private readonly UInt32 pad5;
    };
    #endregion
}