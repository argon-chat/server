namespace Argon.Extensions;

public static class EnumEx
{
    public static T? GetAttributeOfType<T>(this Enum enumValue) where T : Attribute
    {
        var type       = enumValue.GetType();
        var memberInfo = type.GetMember(enumValue.ToString());

        if (memberInfo.Length > 0)
        {
            var attrs = memberInfo[0].GetCustomAttributes(typeof(T), false);
            if (attrs.Length > 0)
                return (T)attrs[0];
        }

        return null;
    }
}