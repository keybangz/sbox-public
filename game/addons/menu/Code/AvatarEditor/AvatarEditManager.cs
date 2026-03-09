using Sandbox;
using static Sandbox.ClothingContainer;

public sealed partial class AvatarEditManager : Component
{
	// Debug logging helper
	private static void AvatarLog( string message )
	{
		var timestamp = DateTime.Now.ToString( "HH:mm:ss.fff" );
		var logLine = $"[AVATAR {timestamp}] {message}";
		Log.Info( logLine );
		try
		{
			System.IO.File.AppendAllText( "/tmp/sbox_avatar.log", logLine + "\n" );
		}
		catch { }
	}

	[Header( "Bodies" )]
	[Property] public GameObject Citizen { get; set; }
	[Property] public GameObject Human { get; set; }
	[Property]
	public bool CitizenActive
	{
		get => !Container.PrefersHuman;
		set => Container.PrefersHuman = !value;
	}

	string lastSaved;
	public ClothingContainer Container { get; set; } = new ClothingContainer();
	public ClothingContainer PreviewContainer { get; set; } = new ClothingContainer();

	// Track if we have pending async operations
	private bool _hasPendingAsyncOperations = false;
	private int _pendingAsyncCount = 0;

	protected override void OnAwake()
	{
		AvatarLog( "OnAwake() START" );
		try
		{
			AvatarLog( "  Calling BuildSteamInventoryClothing..." );
			BuildSteamInventoryClothing();
			AvatarLog( $"  BuildSteamInventoryClothing complete. Found {allClothing.Count} clothing items." );

			AvatarLog( "  Calling ClothingContainer.CreateFromLocalUser..." );
			Container = ClothingContainer.CreateFromLocalUser();
			AvatarLog( $"  CreateFromLocalUser complete. Container has {Container.Clothing.Count} items." );

			AvatarLog( "  Calling Container.Serialize..." );
			lastSaved = Container.Serialize();
			AvatarLog( $"  Serialize complete. Length={lastSaved?.Length ?? 0}" );

			AvatarLog( "  Calling ApplyChangesToModel..." );
			ApplyChangesToModel();
			AvatarLog( "  ApplyChangesToModel complete." );

			AvatarLog( "  Calling AvatarBackgroundRig.RestoreSaved..." );
			AvatarBackgroundRig.RestoreSaved();
			AvatarLog( "  RestoreSaved complete." );

			AvatarLog( "OnAwake() END - SUCCESS" );
		}
		catch ( Exception e )
		{
			AvatarLog( $"OnAwake() EXCEPTION: {e.GetType().Name}: {e.Message}" );
			AvatarLog( $"  Stack: {e.StackTrace}" );
			throw;
		}
	}

	protected override void OnEnabled()
	{
		AvatarLog( "OnEnabled() START" );
		try
		{
			base.OnEnabled();
			AvatarLog( $"OnEnabled() END - Citizen={Citizen?.IsValid()}, Human={Human?.IsValid()}" );
		}
		catch ( Exception e )
		{
			AvatarLog( $"OnEnabled() EXCEPTION: {e.GetType().Name}: {e.Message}" );
			throw;
		}
	}

	protected override void OnDisabled()
	{
		AvatarLog( "OnDisabled() START" );
		AvatarLog( $"  PendingAsyncCount={_pendingAsyncCount}, HasPending={_hasPendingAsyncOperations}" );
		AvatarLog( $"  Citizen={Citizen?.IsValid()}, Human={Human?.IsValid()}" );
		AvatarLog( $"  Container.Clothing.Count={Container?.Clothing?.Count ?? -1}" );
		try
		{
			base.OnDisabled();
			AvatarLog( "OnDisabled() END - SUCCESS" );
		}
		catch ( Exception e )
		{
			AvatarLog( $"OnDisabled() EXCEPTION: {e.GetType().Name}: {e.Message}" );
			AvatarLog( $"  Stack: {e.StackTrace}" );
			throw;
		}
	}

	protected override void OnDestroy()
	{
		AvatarLog( "OnDestroy() START" );
		AvatarLog( $"  PendingAsyncCount={_pendingAsyncCount}, HasPending={_hasPendingAsyncOperations}" );
		AvatarLog( $"  Citizen={Citizen?.IsValid()}, Human={Human?.IsValid()}" );
		AvatarLog( $"  Container.Clothing.Count={Container?.Clothing?.Count ?? -1}" );
		AvatarLog( $"  allClothing.Count={allClothing?.Count ?? -1}" );
		AvatarLog( $"  Thread={System.Threading.Thread.CurrentThread.ManagedThreadId}" );
		try
		{
			// Log the state of child GameObjects (clothing items)
			if ( Citizen?.IsValid() == true )
			{
				var citizenChildren = Citizen.Children.Count();
				AvatarLog( $"  Citizen children (clothing): {citizenChildren}" );
			}
			if ( Human?.IsValid() == true )
			{
				var humanChildren = Human.Children.Count();
				AvatarLog( $"  Human children (clothing): {humanChildren}" );
			}

			// Clear references to help GC and prevent dangling refs
			AvatarLog( "  Clearing Container..." );
			Container = null;
			PreviewContainer = null;

			AvatarLog( "  Clearing allClothing list..." );
			allClothing?.Clear();
			allClothing = null;

			AvatarLog( "OnDestroy() END - SUCCESS" );
		}
		catch ( Exception e )
		{
			AvatarLog( $"OnDestroy() EXCEPTION: {e.GetType().Name}: {e.Message}" );
			AvatarLog( $"  Stack: {e.StackTrace}" );
			throw;
		}
	}

	List<Clothing> allClothing = new();

	void BuildSteamInventoryClothing()
	{
		AvatarLog( "  BuildSteamInventoryClothing START" );
		try
		{
			AvatarLog( "    Getting all Clothing from ResourceLibrary..." );
			int localCount = 0;
			foreach ( var c in ResourceLibrary.GetAll<Clothing>() )
			{
				if ( !c.ResourcePath.StartsWith( "models/citizen_clothes/" ) ) continue;
				allClothing.Add( c );
				localCount++;
			}
			AvatarLog( $"    Found {localCount} local clothing resources." );

			AvatarLog( "    Getting Steam Inventory Definitions..." );
			int steamCount = 0;
			foreach ( var item in Sandbox.Services.Inventory.Definitions )
			{
				// Don't include any definitions that we already have as Clothing resources
				if ( allClothing.Any( x => x.SteamItemDefinitionId == item.Id ) )
					continue;

				var clothing = new Clothing();
				clothing.Title = item.Name;
				clothing.Category = Enum.TryParse<Clothing.ClothingCategory>( item.Category, out var category ) ? category : Clothing.ClothingCategory.HairLong;
				clothing.Icon = new Clothing.IconSetup() { Path = item.IconUrl };
				clothing.SteamItemDefinitionId = item.Id;

				if ( item.SellStart != null && item.SellStart > DateTime.UtcNow && !IsPurchased( clothing ) )
					continue;

				allClothing.Add( clothing );
				steamCount++;
			}
			AvatarLog( $"    Found {steamCount} Steam inventory items." );
			AvatarLog( "  BuildSteamInventoryClothing END" );
		}
		catch ( Exception e )
		{
			AvatarLog( $"  BuildSteamInventoryClothing EXCEPTION: {e.GetType().Name}: {e.Message}" );
			throw;
		}
	}

	public IEnumerable<Clothing> GetAllClothing()
	{
		return allClothing;
	}

	protected override void OnUpdate()
	{
		Citizen.Enabled = CitizenActive;
		Human.Enabled = !CitizenActive;

		var active = CitizenActive ? Citizen : Human;
		var renderer = active.GetComponent<SkinnedModelRenderer>();

		UpdateEyes( renderer );
		UpdateCamera( renderer );
	}

	public bool IsSelected( Clothing clothing ) => Container.Has( clothing );

	public bool IsPurchased( Clothing item )
	{
		if ( !item.SteamItemDefinitionId.HasValue )
			return true;

		if ( Sandbox.Services.Inventory.HasItem( item.SteamItemDefinitionId.Value ) )
		{
			return true;
		}

		return false;
	}

	public string DisplayName
	{
		get => Container.DisplayName;
		set => Container.DisplayName = value;
	}

	public float Height
	{
		get => Container.Height;
		set
		{
			Container.Height = value;
			ApplyChangesToModel();
		}
	}

	public float Age
	{
		get => Container.Age;
		set
		{
			Container.Age = value;
			ApplyChangesToModel();
		}
	}

	public float Tint
	{
		get => Container.Tint;
		set
		{
			Container.Tint = value;
			ApplyChangesToModel();
		}
	}

	public void PreviewPackage( Package package )
	{
		if ( package == null )
		{
			RevertHovered();
			return;
		}

		MenuUtility.RunTask( () => PreviewPackageAsync( package ) );
	}

	public async Task PreviewPackageAsync( Package package )
	{
		var clothing = await Cloud.Load<Clothing>( package.FullIdent );
		OnClothingHover( clothing );
	}

	public void OnClothingHover( Clothing clothing )
	{
		if ( clothing == null )
		{
			RevertHovered();
			return;
		}

		PreviewContainer.Deserialize( Container.Serialize() );

		if ( !PreviewContainer.Has( clothing ) )
		{
			PreviewContainer.Toggle( clothing );
		}

		ApplyPreviewToModel();
	}

	public void SetTint( Clothing clothing, float f )
	{
		ClothingEntry e = Container.FindEntry( clothing );
		if ( e is null ) return;

		e.Tint = f;
		ApplyChangesToModel();
	}

	public float GetTint( Clothing clothing )
	{
		ClothingEntry e = Container.FindEntry( clothing );
		if ( e is not null && e.Tint.HasValue )
		{
			return e.Tint.Value;
		}

		return clothing.TintDefault;
	}

	public void OnClothingToggle( Clothing clothing )
	{
		if ( !IsPurchased( clothing ) )
		{
			// TODO - Pop up a shopping cart HA HA HA
			return;
		}

		RevertHovered();

		Container.Toggle( clothing );
		ApplyChangesToModel();
	}

	public void ApplyPreviewToModel()
	{
		AvatarLog( "ApplyPreviewToModel() called" );
		// We have to run it this way so it'll be in the menu context
		MenuUtility.RunTask( () => ApplyAsync( PreviewContainer, Citizen.GetComponent<SkinnedModelRenderer>( true ), "Citizen-Preview" ) );
		MenuUtility.RunTask( () => ApplyAsync( PreviewContainer, Human.GetComponent<SkinnedModelRenderer>( true ), "Human-Preview" ) );
	}

	public void ApplyChangesToModel()
	{
		AvatarLog( "ApplyChangesToModel() called" );
		// We have to run it this way so it'll be in the menu context
		MenuUtility.RunTask( () => ApplyAsync( Container, Citizen.GetComponent<SkinnedModelRenderer>( true ), "Citizen" ) );
		MenuUtility.RunTask( () => ApplyAsync( Container, Human.GetComponent<SkinnedModelRenderer>( true ), "Human" ) );
	}

	async Task ApplyAsync( ClothingContainer container, SkinnedModelRenderer targetRenderer, string targetName )
	{
		AvatarLog( $"ApplyAsync({targetName}) START - Thread={System.Threading.Thread.CurrentThread.ManagedThreadId}" );

		// Track pending async operations
		System.Threading.Interlocked.Increment( ref _pendingAsyncCount );
		_hasPendingAsyncOperations = true;

		try
		{
			if ( targetRenderer == null )
			{
				AvatarLog( $"ApplyAsync({targetName}) - targetRenderer is NULL, skipping" );
				return;
			}
			if ( !targetRenderer.IsValid() )
			{
				AvatarLog( $"ApplyAsync({targetName}) - targetRenderer is INVALID, skipping" );
				return;
			}

			// Check if component is still valid before async operation
			if ( !this.IsValid() )
			{
				AvatarLog( $"ApplyAsync({targetName}) - Component is no longer valid, aborting" );
				return;
			}

			AvatarLog( $"ApplyAsync({targetName}) - Calling container.ApplyAsync..." );
			await container.ApplyAsync( targetRenderer, default );

			// Check again after async operation completes
			if ( !this.IsValid() )
			{
				AvatarLog( $"ApplyAsync({targetName}) - Component became invalid during async operation" );
				return;
			}

			AvatarLog( $"ApplyAsync({targetName}) END - SUCCESS" );
		}
		catch ( Exception e )
		{
			AvatarLog( $"ApplyAsync({targetName}) EXCEPTION: {e.GetType().Name}: {e.Message}" );
			AvatarLog( $"  Stack: {e.StackTrace}" );
			throw;
		}
		finally
		{
			var remaining = System.Threading.Interlocked.Decrement( ref _pendingAsyncCount );
			AvatarLog( $"ApplyAsync({targetName}) FINALLY - remaining pending: {remaining}" );
			if ( remaining <= 0 )
			{
				_hasPendingAsyncOperations = false;
			}
		}
	}

	void RevertHovered()
	{
		ApplyChangesToModel();
	}

	public bool HasUnsavedChanges => lastSaved != Container.Serialize();

	public void SaveChanges()
	{
		lastSaved = Container.Serialize();
		ApplyChangesToModel();

		_ = MenuUtility.SaveAvatar( Container, true, 0 );
	}

	public void RevertChanges()
	{
		Container.Deserialize( lastSaved );
		ApplyChangesToModel();
	}
}


public struct ColorSwatch
{
	public float Value;
	public Color Color;
}
