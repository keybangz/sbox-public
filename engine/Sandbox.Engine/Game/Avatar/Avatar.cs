namespace Sandbox;

internal class Avatar
{
	// Updated to use path-based format ("p") instead of legacy integer IDs ("id")
	// which were hashed using an older algorithm that's no longer compatible.
	// Default outfit: short scruffy hair, eyebrows, t-shirt, jeans, trainers
	const string DefaultAvatar = "{\"Items\":[" +
		"{\"p\":\"models/citizen_clothes/hair/hair_shortscruffy/hair_shortscruffy_brown.clothing\"}," +
		"{\"p\":\"models/citizen_clothes/hair/eyebrows/eyebrows.clothing\"}," +
		"{\"p\":\"models/citizen_clothes/shirt/tshirt/tshirt.clothing\"}," +
		"{\"p\":\"models/citizen_clothes/trousers/jeans/jeans.clothing\"}," +
		"{\"p\":\"models/citizen_clothes/shoes/trainers/trainers.clothing\"}" +
		"],\"Height\":0.5}";

	[ConVar( "avatar", ConVarFlags.Saved | ConVarFlags.UserInfo | ConVarFlags.Protected )]
	public static string AvatarJson { get; set; } = DefaultAvatar;
}
