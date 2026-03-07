using Sandbox.Rendering;

namespace Sandbox.UI;

internal sealed partial class PanelRenderer
{
	[ConVar( ConVarFlags.Protected, Help = "Enable drawing text" )]
	public static bool ui_drawtext { get; set; } = true;

	public Rect Screen { get; internal set; }

	/// <summary>
	/// Build command lists for a root panel and all its children.
	/// Called during the tick phase, before rendering.
	/// </summary>
	public void BuildCommandLists( RootPanel panel, float opacity = 1.0f )
	{
		Screen = panel.PanelBounds;

		MatrixStack.Clear();
		MatrixStack.Push( Matrix.Identity );
		Matrix = Matrix.Identity;

		RenderModeStack.Clear();
		RenderModeStack.Push( "normal" );
		SetRenderMode( "normal" );

		DefaultRenderTarget = Graphics.RenderTarget;

		LayerStack?.Clear();

		InitScissor( Screen );

		// Reset time budget for this frame's BuildCommandLists
		ResetBuildCLTimeBudget();

		BuildCommandLists( (Panel)panel, new RenderState { X = Screen.Left, Y = Screen.Top, Width = Screen.Width, Height = Screen.Height, RenderOpacity = opacity } );
	}

	private static int _buildCLPanelCount = 0;
	private static long _buildCLStartTime = 0;
	private static bool _buildCLTimeBudgetExceeded = false;
	private const long BuildCLTimeBudgetMs = 50;

	internal static void ResetBuildCLTimeBudget()
	{
		// Log if previous frame had a long build time
		if ( _buildCLPanelCount > 500 )
		{
			var totalTime = System.Environment.TickCount64 - _buildCLStartTime;
			if ( totalTime > 100 )
				System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[BUILDCL_RESET] Previous: {_buildCLPanelCount} panels in {totalTime}ms, exceeded={_buildCLTimeBudgetExceeded}\n" );
		}

		_buildCLStartTime = System.Environment.TickCount64;
		_buildCLTimeBudgetExceeded = false;
		_buildCLPanelCount = 0;
	}

	/// <summary>
	/// Build command lists for a panel and its children.
	/// </summary>
	public void BuildCommandLists( Panel panel, RenderState state )
	{
		if ( panel?.ComputedStyle == null )
			return;

		if ( !panel.IsVisible )
			return;

		// Check time budget FIRST - if exceeded from previous processing, bail out immediately
		if ( _buildCLTimeBudgetExceeded )
		{
			panel.IsRenderDirty = true;
			return;
		}

		// Time budget check every 10 panels to catch overruns quickly
		_buildCLPanelCount++;
		if ( _buildCLPanelCount % 10 == 0 )
		{
			var elapsed = System.Environment.TickCount64 - _buildCLStartTime;
			if ( elapsed > BuildCLTimeBudgetMs )
			{
				_buildCLTimeBudgetExceeded = true;
				// Log when we exceed budget significantly
				if ( elapsed > 1000 )
				{
					System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[TIME_BUDGET] BuildCL exceeded: {elapsed}ms after {_buildCLPanelCount} panels\n" );
				}
				panel.IsRenderDirty = true;
				return;
			}
		}

		// Build transform command list (sets GlobalMatrix and TransformMat attribute)
		BuildTransformCommandList( panel );

		// Update clip only when scissor actually changed
		var scissorHash = HashCode.Combine( ScissorGPU.Rect, ScissorGPU.CornerRadius, ScissorGPU.Matrix );
		if ( panel._lastScissorHash != scissorHash )
		{
			panel._lastScissorHash = scissorHash;
			panel.ClipCommandList.Reset();
			SetScissorAttributes( panel.ClipCommandList, ScissorGPU );
		}

		// Track render mode so OverrideBlendMode is correct when baking D_BLENDMODE
		var renderMode = PushRenderMode( panel );

		// Update layer (creates render target if needed for filters/masks)
		var updateLayerStart = System.Environment.TickCount64;
		panel.UpdateLayer( panel.ComputedStyle );
		var updateLayerElapsed = System.Environment.TickCount64 - updateLayerStart;
		if ( updateLayerElapsed > 500 )
		{
			var panelInfo = panel.GetType().FullName;
			System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[UPDATELAYER] {panelInfo} took {updateLayerElapsed}ms\n" );
		}

		// NOTE: We intentionally do NOT check BackgroundImage.IsDirty here anymore
		// because accessing .BackgroundImage triggers lazy Texture.Load() which can block
		// for 10+ seconds. The texture will be loaded when actually rendered.
		// This is a tradeoff: slightly delayed dirty detection vs. 10+ second freezes.

		if ( panel.IsRenderDirty || panel.HasPanelLayer )
		{
			var buildCLStart = System.Environment.TickCount64;
			BuildCommandList( panel, ref state );
			var buildCLElapsed = System.Environment.TickCount64 - buildCLStart;
			if ( buildCLElapsed > 500 )
			{
				var panelInfo = panel.GetType().FullName;
				System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[BUILDCL_SINGLE] {panelInfo} took {buildCLElapsed}ms\n" );
			}

			// Add Content = Text, Image (not children)
			if ( panel.HasContent )
			{
				try
				{
					var drawStart = System.Environment.TickCount64;
					panel.DrawContent( panel.CommandList, this, ref state );
					var drawElapsed = System.Environment.TickCount64 - drawStart;
					if ( drawElapsed > 500 )
					{
						var panelInfo = panel.GetType().FullName;
						if ( !string.IsNullOrEmpty( panel.ElementName ) )
							panelInfo += $"#{panel.ElementName}";
						System.IO.File.AppendAllText( "/tmp/block_debug.txt", $"[DRAWCONTENT] {panelInfo} took {drawElapsed}ms\n" );
					}
				}
				catch ( Exception e )
				{
					Log.Error( e );
				}
			}
		}

		// Build command lists for children BEFORE BuildLayerPopCommands so that the
		// parent stays on the LayerStack while children are built
		if ( panel.HasChildren )
		{
			panel.BuildCommandListsForChildren( this, ref state );
		}

		// Build post-children layer commands (for filters/masks) AFTER children so the
		// parent's LayerStack entry is still present when children call PopLayer
		if ( panel.HasPanelLayer )
		{
			panel.BuildLayerPopCommands( this, DefaultRenderTarget );
		}

		if ( renderMode ) PopRenderMode();
	}

	/// <summary>
	/// Gather all pre-built per-panel command lists into a single global CL.
	/// Called after BuildCommandLists during the tick/simulate phase.
	/// </summary>
	public void GatherCommandLists( RootPanel root, float opacity = 1.0f )
	{
		var globalCL = root.PanelCommandList;
		globalCL.Reset();

		Screen = root.PanelBounds;
		DefaultRenderTarget = Graphics.RenderTarget;

		InitScissor( Screen, globalCL );

		GatherPanel( root, new RenderState
		{
			X = Screen.Left,
			Y = Screen.Top,
			Width = Screen.Width,
			Height = Screen.Height,
			RenderOpacity = opacity
		}, globalCL );
	}

	/// <summary>
	/// Gather a panel's pre-built CL into the global CL, then recurse children.
	/// No culling here — the GPU-side scissor handles clipping. This keeps
	/// the gather purely structural so it can be cached aggressively.
	/// </summary>
	internal void GatherPanel( Panel panel, RenderState state, CommandList globalCL )
	{
		if ( panel?.ComputedStyle == null ) return;
		if ( !panel.IsVisible ) return;

		globalCL.InsertList( panel.CommandList );

		if ( panel.HasChildren )
			panel.GatherChildrenCommandLists( this, ref state, globalCL );

		if ( panel.HasPanelLayer )
		{
			globalCL.SetRenderTarget( DefaultRenderTarget );
			globalCL.InsertList( panel.LayerCommandList );
		}
	}

	internal struct LayerEntry
	{
		public string RTHandle;
		public Matrix Matrix;
	}

	internal Stack<LayerEntry> LayerStack;

	internal bool IsWorldPanel( Panel panel )
	{
		if ( panel is RootPanel { IsWorldPanel: true } )
			return true;

		if ( panel.FindRootPanel()?.IsWorldPanel ?? false )
			return true;

		return false;
	}

	internal void PushLayer( Panel panel, RenderTargetHandle handle, Matrix mat )
	{
		LayerStack ??= new Stack<LayerEntry>();

		panel.CommandList.SetRenderTarget( handle );
		panel.CommandList.Attributes.Set( "LayerMat", mat );
		panel.CommandList.Attributes.SetCombo( "D_WORLDPANEL", 0 );
		panel.CommandList.Clear( Color.Transparent );

		LayerStack.Push( new LayerEntry { RTHandle = handle.Name, Matrix = mat } );
	}

	/// <summary>
	/// Pop a layer and restore the previous render target.
	/// Commands are written to the specified command list.
	/// </summary>
	internal void PopLayer( Panel panel, CommandList commandList, RenderTarget defaultRenderTarget )
	{
		LayerStack.Pop();

		if ( LayerStack.TryPeek( out var top ) )
		{
			commandList.SetRenderTarget( new RenderTargetHandle { Name = top.RTHandle } );
			commandList.Attributes.Set( "LayerMat", top.Matrix );
			commandList.Attributes.SetCombo( "D_WORLDPANEL", 0 );
		}
		else
		{
			commandList.Attributes.Set( "LayerMat", Matrix.Identity );
			commandList.Attributes.SetCombo( "D_WORLDPANEL", IsWorldPanel( panel ) );
		}
	}

	/// <summary>
	/// The default render target for the current root panel render.
	/// Set during Render() and used by layers to restore after popping.
	/// </summary>
	internal RenderTarget DefaultRenderTarget;
}
