namespace Argon.Api.Features.Mapper;

using AutoMapper;
using Contracts;
using Entities;

public class UserMappingProfile : Profile
{
    public UserMappingProfile()
        => CreateMap<UserDto, UserResponse>()
           .ForMember(dest => dest.Username, opt => opt.MapFrom(src => src.Username ?? string.Empty))
           .ForMember(dest => dest.AvatarUrl, opt => opt.MapFrom(src => src.AvatarUrl ?? string.Empty))
           .ForMember(dest => dest.DisplayName, opt => opt.MapFrom(src => src.Username ?? ""))
           .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
           .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
           .DisableCtorValidation()
           .ConstructAutoUninitialized();
}

public class UsersToServerRelationProfile : Profile
{
    public UsersToServerRelationProfile()
        => CreateMap<UsersToServerRelation, UsersToServerRelationDto>()
           .ForMember(dest => dest.User, opt => opt.MapFrom(src => src.User))
           .ForMember(dest => dest.ServerId, opt => opt.MapFrom(src => src.ServerId))
           .ForMember(dest => dest.Joined, opt => opt.MapFrom(src => src.Joined))
           .ForMember(dest => dest.Role, opt => opt.MapFrom(src => src.Role))
           .DisableCtorValidation()
           .ConstructAutoUninitialized();
}

public class ServerProfile : Profile
{
    public ServerProfile()
        => CreateMap<Server, ServerDto>()
           .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
           .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
           .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
           .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
           .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
           .ForMember(dest => dest.AvatarUrl, opt => opt.MapFrom(src => src.AvatarUrl))
           .ForMember(dest => dest.Channels, opt => opt.MapFrom(src => src.Channels))
           .ForMember(dest => dest.Users, opt => opt.MapFrom(src => src.UsersToServerRelations))
           .DisableCtorValidation()
           .ConstructAutoUninitialized();
}

public class UserProfile : Profile
{
    public UserProfile()
        => CreateMap<User, UserDto>()
           .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
           .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
           .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
           .ForMember(dest => dest.Email, opt => opt.MapFrom(src => src.Email))
           .ForMember(dest => dest.Username, opt => opt.MapFrom(src => src.Username))
           .ForMember(dest => dest.PhoneNumber, opt => opt.MapFrom(src => src.PhoneNumber))
           .ForMember(dest => dest.AvatarUrl, opt => opt.MapFrom(src => src.AvatarFileId))
           .ForMember(dest => dest.DeletedAt, opt => opt.MapFrom(src => src.DeletedAt))
           .ForMember(dest => dest.Servers, opt => opt.MapFrom(src => src.UsersToServerRelations.Select(rel => rel.Server)))
           .DisableCtorValidation()
           .ConstructAutoUninitialized();
}

public class ChannelProfile : Profile
{
    public ChannelProfile()
        => CreateMap<Channel, ChannelDto>()
           .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
           .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
           .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
           .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
           .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
           .ForMember(dest => dest.UserId, opt => opt.MapFrom(src => src.UserId))
           .ForMember(dest => dest.ChannelType, opt => opt.MapFrom(src => src.ChannelType))
           .ForMember(dest => dest.AccessLevel, opt => opt.MapFrom(src => src.AccessLevel))
           .ForMember(dest => dest.ServerId, opt => opt.MapFrom(src => src.ServerId))
           .ForMember(dest => dest.ConnectedUsers, opt => opt.Ignore())
           .DisableCtorValidation()
           .ConstructAutoUninitialized();
}

public class ServerMappingProfile : Profile
{
    public ServerMappingProfile()
    {
        CreateMap<ServerDto, ServerDefinition>()
           .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
           .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description ?? string.Empty))
           .ForMember(dest => dest.AvatarUrl, opt => opt.MapFrom(src => src.AvatarUrl ?? string.Empty))
           .ForMember(dest => dest.Channels, opt => opt.MapFrom(src => src.Channels))
           .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
           .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
           .DisableCtorValidation()
           .ConstructAutoUninitialized();

        CreateMap<ChannelDto, ChannelDefinition>()
           .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
           .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
           .ForMember(dest => dest.CreatedBy, opt => opt.MapFrom(src => src.UserId.ToString()))
           .ForMember(dest => dest.ChannelType, opt => opt.MapFrom(src => src.ChannelType.ToString()))
           .ForMember(dest => dest.AccessLevel, opt => opt.MapFrom(src => src.AccessLevel.ToString()))
           .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
           .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
           .DisableCtorValidation()
           .ConstructAutoUninitialized();
    }
}