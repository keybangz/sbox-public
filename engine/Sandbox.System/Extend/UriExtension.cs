using System.Net.Sockets;
using System.Net;
using System.Collections.Concurrent;

namespace Sandbox;

public static partial class SandboxSystemExtensions
{
	// Cache DNS results to avoid repeated slow lookups
	private static readonly ConcurrentDictionary<string, (bool IsPrivate, DateTime CacheTime)> _dnsCache = new();
	private static readonly TimeSpan _dnsCacheExpiry = TimeSpan.FromMinutes( 5 );

	// Known public domains that don't need DNS checks (Facepunch CDN, Steam, etc.)
	private static readonly HashSet<string> _knownPublicDomains = new( StringComparer.OrdinalIgnoreCase )
	{
		"facepunch.com",
		"sbox.facepunch.com",
		"asset.party",
		"files.facepunch.com",
		"steamcommunity.com",
		"steamstatic.com",
		"steampowered.com",
		"cloudflare.com",
		"githubusercontent.com",
		"github.com",
		"s3.amazonaws.com",
	};

	/// <summary>
	/// Does this Uri resolve to a private range IP address?
	/// Uses caching to avoid slow repeated DNS lookups on Linux.
	/// </summary>
	internal static bool IsPrivate( this Uri uri )
	{
		var host = uri.DnsSafeHost;

		// Fast path: skip DNS for known public domains
		foreach ( var domain in _knownPublicDomains )
		{
			if ( host.EndsWith( domain, StringComparison.OrdinalIgnoreCase ) )
				return false;
		}

		// Check cache first
		if ( _dnsCache.TryGetValue( host, out var cached ) &&
		     DateTime.UtcNow - cached.CacheTime < _dnsCacheExpiry )
		{
			return cached.IsPrivate;
		}

		// Perform DNS lookup with timeout
		try
		{
			var task = Dns.GetHostEntryAsync( host );
			if ( !task.Wait( TimeSpan.FromSeconds( 2 ) ) )
			{
				// DNS lookup timed out - assume not private and cache
				_dnsCache[host] = (false, DateTime.UtcNow);
				return false;
			}

			var entry = task.Result;
			var isPrivate = entry.AddressList.Any( x => x.IsPrivate() );
			_dnsCache[host] = (isPrivate, DateTime.UtcNow);
			return isPrivate;
		}
		catch
		{
			// DNS lookup failed - cache as not private
			_dnsCache[host] = (false, DateTime.UtcNow);
			return false;
		}
	}

	/// <summary>
	/// Returns true if the IP address is in a private range.<br/>
	/// IPv4: Loopback, link local ("169.254.x.x"), class A ("10.x.x.x"), class B ("172.16.x.x" to "172.31.x.x") and class C ("192.168.x.x").<br/>
	/// IPv6: Loopback, link local, site local, unique local and private IPv4 mapped to IPv6.<br/>
	/// </summary>
	internal static bool IsPrivate( this IPAddress ip )
	{
		// Map back to IPv4 if mapped to IPv6, for example "::ffff:1.2.3.4" to "1.2.3.4".
		if ( ip.IsIPv4MappedToIPv6 )
			ip = ip.MapToIPv4();

		// Checks loopback ranges for both IPv4 and IPv6.
		if ( IPAddress.IsLoopback( ip ) ) return true;

		// IPv4
		if ( ip.AddressFamily == AddressFamily.InterNetwork )
		{
			var ipv4Bytes = ip.GetAddressBytes();

			// Link local (no IP assigned by DHCP): 169.254.0.0 to 169.254.255.255 (169.254.0.0/16)
			bool IsLinkLocal() => ipv4Bytes[0] == 169 && ipv4Bytes[1] == 254;

			// Class A private range: 10.0.0.0 – 10.255.255.255 (10.0.0.0/8)
			bool IsClassA() => ipv4Bytes[0] == 10;

			// Class B private range: 172.16.0.0 – 172.31.255.255 (172.16.0.0/12)
			bool IsClassB() => ipv4Bytes[0] == 172 && ipv4Bytes[1] >= 16 && ipv4Bytes[1] <= 31;

			// Class C private range: 192.168.0.0 – 192.168.255.255 (192.168.0.0/16)
			bool IsClassC() => ipv4Bytes[0] == 192 && ipv4Bytes[1] == 168;

			return IsLinkLocal() || IsClassA() || IsClassC() || IsClassB();
		}

		// IPv6
		if ( ip.AddressFamily == AddressFamily.InterNetworkV6 )
		{
			return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
		}

		throw new NotSupportedException( $"IP address family {ip.AddressFamily} is not supported, expected only IPv4 (InterNetwork) or IPv6 (InterNetworkV6)" );
	}
}
