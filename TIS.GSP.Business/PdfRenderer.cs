using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GalleryServer.Events.CustomExceptions;

namespace GalleryServer.Business
{
  /// <summary>
  /// Renders the first page of a PDF file to a bitmap in-process using PDFium (through PdfiumViewer), so no external executable
  /// such as ImageMagick or Ghostscript is needed. Requires Full Trust and the native library at bin\x64\pdfium.dll (64-bit app pool)
  /// or bin\x86\pdfium.dll (32-bit app pool). When the library is not present, <see cref="RenderFirstPage" /> quietly returns null
  /// so the caller can fall back to another method.
  /// </summary>
  public static class PdfRenderer
  {
    // PDFium is not thread-safe, so all use of it is serialized.
    private static readonly object _lock = new object();
    private static bool _isUnavailable;
    private static bool _isNativeLibraryLoaded;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    /// <summary>
    /// Renders the first page of the PDF at <paramref name="pdfPath" /> so that its longest side is <paramref name="maxLength" /> pixels.
    /// The caller is responsible for disposing the returned bitmap. Returns null when PDFium is not available or the file cannot be
    /// rendered; in the latter case the error is recorded in the event log.
    /// </summary>
    /// <param name="pdfPath">The full path to the PDF file.</param>
    /// <param name="maxLength">The length, in pixels, of the longest side of the rendered image.</param>
    /// <param name="galleryId">The gallery ID, used to associate any logged error with the gallery.</param>
    /// <returns>A <see cref="Bitmap" />, or null.</returns>
    public static Bitmap RenderFirstPage(string pdfPath, int maxLength, int galleryId)
    {
      if (_isUnavailable || AppSetting.Instance.AppTrustLevel != ApplicationTrustLevel.Full || !File.Exists(pdfPath))
      {
        return null;
      }

      lock (_lock)
      {
        if (_isUnavailable)
        {
          return null;
        }

        try
        {
          if (!_isNativeLibraryLoaded && !LoadNativeLibrary(galleryId))
          {
            return null;
          }

          return RenderFirstPageCore(pdfPath, maxLength);
        }
        catch (Exception ex)
        {
          // These indicate the library itself cannot be used (as opposed to a problem with this one PDF), so stop trying.
          if (ex is DllNotFoundException || ex is BadImageFormatException || ex is FileNotFoundException || ex is FileLoadException || ex is TypeInitializationException)
          {
            _isUnavailable = true;
          }

          ex.Data.Add("GSP Info", String.Format("PDFium could not render the file {0}. A different thumbnail method or a generic thumbnail image will be used instead.", pdfPath));
          Events.EventController.RecordError(ex, AppSetting.Instance, galleryId, Factory.LoadGallerySettings());

          return null;
        }
      }
    }

    // Isolated in its own method so a missing PdfiumViewer.dll surfaces as an exception inside the try/catch above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Bitmap RenderFirstPageCore(string pdfPath, int maxLength)
    {
      using (var doc = PdfiumViewer.PdfDocument.Load(pdfPath))
      {
        if (doc.PageCount < 1)
        {
          return null;
        }

        var pageSize = doc.PageSizes[0];
        if (pageSize.Width <= 0 || pageSize.Height <= 0)
        {
          return null;
        }

        var scale = maxLength / Math.Max(pageSize.Width, pageSize.Height);
        var width = Math.Max(1, (int)Math.Round(pageSize.Width * scale));
        var height = Math.Max(1, (int)Math.Round(pageSize.Height * scale));

        using (var rendered = doc.Render(0, width, height, 96, 96, PdfiumViewer.PdfRenderFlags.Annotations))
        {
          // Copy so the result does not depend on PDFium's native buffer after the document is disposed.
          return new Bitmap(rendered);
        }
      }
    }

    /// <summary>
    /// Loads pdfium.dll by its full path. Doing this up front avoids relying on PdfiumViewer locating the file itself, which is
    /// unreliable when ASP.NET shadow-copies the assemblies. Returns false, without logging, when the file simply isn't deployed.
    /// </summary>
    private static bool LoadNativeLibrary(int galleryId)
    {
      var path = Path.Combine(AppSetting.Instance.PhysicalApplicationPath, "bin", (IntPtr.Size == 8 ? "x64" : "x86"), "pdfium.dll");

      if (!File.Exists(path))
      {
        _isUnavailable = true;
        return false;
      }

      if (LoadLibrary(path) == IntPtr.Zero)
      {
        _isUnavailable = true;

        var ex = new BusinessException(String.Format("PDFium ({0}) could not be loaded (Win32 error {1}). PDF thumbnails are created with another method instead.", path, Marshal.GetLastWin32Error()));
        Events.EventController.RecordError(ex, AppSetting.Instance, galleryId, Factory.LoadGallerySettings());

        return false;
      }

      _isNativeLibraryLoaded = true;
      return true;
    }
  }
}
