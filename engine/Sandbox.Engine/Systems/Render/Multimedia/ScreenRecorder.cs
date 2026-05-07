using NativeEngine;
using System.Runtime.InteropServices;

namespace Sandbox;

/// <summary>
/// Records the screen to a video file.
/// </summary>
internal static class ScreenRecorder
{
	private static bool _isRecording;
	private static bool _firstFrame;
	private static string _filename;
	private static VideoWriter _videoWriter;
	private static RealTimeSince _recordingTimer;
	private static float _nextCaptureTime;
	private static object _timerLockObj = new();

	/// <summary>
	/// Gets whether a recording is currently in progress.
	/// </summary>
	public static bool IsRecording() => _isRecording;

	[ConVar( "video_bitrate", Min = 1, Max = 100, Help = "Bit rate for video recorder (in Mbps)" )]
	public static int VideoBitRate { get; set; } = 10;
	[ConVar( "video_framerate", Min = 1, Max = 1000, Help = "Frame rate for screen recording" )]
	public static int VideoFrameRate { get; set; } = 60;
	[ConVar( "video_Scale", Min = 1, Max = 800, Help = "Scale percentage for video recorder" )]
	public static float VideoScale { get; set; } = 100f;
	[ConVar( "video_codec", Help = "Video codec for screen recording" )]
	public static VideoWriter.Codec VideoCodec { get; set; } = VideoWriter.Codec.AV1;
	[ConVar( "video_audio_codec", Help = "Audio codec for screen recording" )]
	public static VideoWriter.AudioCodec AudioCodec { get; set; } = VideoWriter.AudioCodec.Opus;

	/// <summary>
	/// Starts recording to the specified file.
	/// </summary>
	/// <returns>True if recording started successfully</returns>
	[ConCmd( "video" )]
	public static bool StartRecording()
	{
		if ( _isRecording )
		{
			StopRecording();
			return false;
		}

		_isRecording = true;
		_filename = ScreenCaptureUtility.GenerateScreenshotFilename( "mp4" );
		_firstFrame = true;

		Log.Info( $"Video recording started: {_filename}" );
		return true;
	}

	/// <summary>
	/// Stops the current recording.
	/// </summary>
	public static void StopRecording()
	{
		if ( !_isRecording ) return;

		_isRecording = false;

		if ( _videoWriter != null )
		{
			_videoWriter.Dispose();
			_videoWriter = null;
		}

		Log.Info( $"Video recording finished: <a href=\"{_filename}\">{_filename}</a>" );
	}

	/// <summary>
	/// Captures a video frame from the provided render context and view.
	/// </summary>
	public static void RecordVideoFrame( IRenderContext renderContext, ITexture nativeTexture )
	{
		if ( !_isRecording ) return;
		if ( nativeTexture.IsNull || !nativeTexture.IsStrongHandleValid() ) return;

		if ( _videoWriter == null )
		{
			try
			{
				var desc = g_pRenderDevice.GetOnDiskTextureDesc( nativeTexture );

				_videoWriter = new VideoWriter( _filename, new VideoWriter.Config
				{
					Width = desc.m_nWidth,
					Height = desc.m_nHeight,
					FrameRate = VideoFrameRate,
					Bitrate = VideoBitRate,
					Codec = VideoCodec,
					Container = VideoWriter.Container.MP4,
					Preset = VideoWriter.EncodingPreset.Fast,
					AudioCodec = AudioCodec
				} );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"Failed to start video recording: {ex.Message}" );
				_isRecording = false;
				return;
			}
		}

		renderContext.ReadTextureAsync( nativeTexture, ( pData, format, mipLevel, width, height, _ ) =>
		{
			if ( !_isRecording || _videoWriter == null ) return;

			// Capture timestamp early to get an accurate timestamp
			// need to lock as multiple threads may call this in parallel
			float timestamp;
			var frameInterval = 1.0f / VideoFrameRate;
			lock ( _timerLockObj )
			{
				if ( _firstFrame )
				{
					_recordingTimer = 0;
					_nextCaptureTime = frameInterval;
					_firstFrame = false;
					timestamp = 0;
				}
				else
				{
					var now = (float)_recordingTimer;

					// Skip frames that arrive before the next scheduled frame time
					if ( now < _nextCaptureTime )
						return;

					timestamp = now;

					// Schedule the next frame (additive to prevent drift)
					_nextCaptureTime += frameInterval;
				}
			}

// Due to thread races:
			// Writer may be null very briefly during shutdown, instead of adding complex locks just handle the exception.
			try
			{
				// Skip frames with mismatched resolution (can happen during resize or with ScenePanels)
				if ( width == _videoWriter.Width && height == _videoWriter.Height )
				{
					_videoWriter.AddFrame( pData, TimeSpan.FromSeconds( timestamp ) );
				}
			}
			catch ( NullReferenceException )
			{
			}
		} );
	}

	/// <summary>
	/// Captures an audio frame from the provided audio buffers.
	/// </summary>
	public static void RecordAudioSample( CAudioMixDeviceBuffers buffers )
	{
		// Drop until video is ready
		if ( !_isRecording || _videoWriter == null ) return;

		// Due to thread races:
		// Writer may be null very briefly during shutdown, instead of adding complex locks just handle the exception.
		try
		{
			_videoWriter.AddAudioSamples( buffers );
		}
		catch ( NullReferenceException )
		{
	}

}
}
