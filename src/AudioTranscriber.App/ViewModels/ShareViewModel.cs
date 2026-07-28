using System.Collections.ObjectModel;
using System.Net.Http;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// ViewModel de "Compartir proyecto" (Team Sharing slice 1b, Phase 17 "UI de compartir
/// proyectos"): invitar por email, ver/cambiar el rol de los miembros actuales, sacarlos, y ver/
/// cancelar las invitaciones pendientes que YA mandaste para este proyecto. Mismo criterio que
/// <see cref="InvitesViewModel"/>: cero lógica de permisos acá (regla dura del pedido) -- este
/// archivo no tiene ningún <c>if (role == ...)</c> que decida qué puede hacer el usuario actual,
/// solo pide/muestra lo que el servidor manda y deja que rechace lo que no corresponda. Ocultar
/// los controles del dueño (<see cref="MemberVm.IsOwner"/>) es comodidad de UI, no seguridad: el
/// servidor igual lo rechazaría si se lo mandara.
/// </summary>
public partial class ShareViewModel : ObservableObject
{
    /// <summary>Roles asignables al invitar o cambiar rol -- "owner" queda afuera a propósito: es
    /// único por proyecto y el servidor no lo acepta acá.</summary>
    public static readonly string[] AssignableRoles = { "admin", "editor", "viewer" };

    private readonly string _projectId;

    public string ProjectTitle { get; }

    public ObservableCollection<MemberVm> Members { get; } = new();
    public ObservableCollection<SentInviteVm> SentInvites { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _newMemberEmail = string.Empty;

    [ObservableProperty]
    private string _newMemberRole = "viewer";

    [ObservableProperty]
    private bool _isInviting;

    [ObservableProperty]
    private string _inviteErrorMessage = string.Empty;

    public ShareViewModel(string projectId, string projectTitle)
    {
        _projectId = projectId;
        ProjectTitle = projectTitle;
    }

    private static async Task<string> GetAccessTokenOrThrowAsync()
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Iniciá sesión desde 'Sincronización' para compartir el proyecto.");
        return token;
    }

    private static MembersApiClient BuildMembersClient() =>
        new(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);

    private static InvitesApiClient BuildInvitesClient() =>
        new(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        SyncApiException => ex.Message,
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    /// <summary>Trae miembros actuales + invitaciones pendientes enviadas para este proyecto. Se
    /// dispara al abrir <c>ShareWindow</c> y con "Actualizar".</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var membersClient = BuildMembersClient();
            var invitesClient = BuildInvitesClient();

            var members = await membersClient.GetMembersAsync(token, _projectId);
            var sentInvites = await invitesClient.GetSentInvitesAsync(token, _projectId);

            Members.Clear();
            foreach (var member in members.OrderByDescending(m => m.Role == "owner").ThenBy(m => m.Email))
                Members.Add(new MemberVm(member));

            SentInvites.Clear();
            foreach (var invite in sentInvites)
                SentInvites.Add(new SentInviteVm(invite));
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInvite() => !IsInviting && !string.IsNullOrWhiteSpace(NewMemberEmail);

    /// <summary>Invita por email (<see cref="MembersApiClient.InviteMemberAsync"/>). Si la persona
    /// no tiene cuenta el servidor contesta 404 con un mensaje ya humano ("Esa persona todavía no
    /// tiene una cuenta en el producto.") -- este método no lo reescribe, solo lo muestra (regla
    /// dura del pedido: nunca una ventana muda ni un stack trace).</summary>
    [RelayCommand(CanExecute = nameof(CanInvite))]
    private async Task InviteAsync()
    {
        InviteErrorMessage = string.Empty;
        IsInviting = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = BuildMembersClient();
            await client.InviteMemberAsync(token, _projectId, NewMemberEmail.Trim(), NewMemberRole);

            NewMemberEmail = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            InviteErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsInviting = false;
        }
    }

    [RelayCommand]
    private async Task ChangeRoleAsync(MemberVm? member)
    {
        if (member is null || member.IsBusy || member.IsOwner)
            return;
        if (member.SelectedRole == member.Role)
            return;

        member.IsBusy = true;
        member.ErrorMessage = string.Empty;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = BuildMembersClient();
            await client.ChangeRoleAsync(token, _projectId, member.UserId, member.SelectedRole);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            member.SelectedRole = member.Role;
            member.IsBusy = false;
            member.ErrorMessage = FriendlyMessage(ex);
        }
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(MemberVm? member)
    {
        if (member is null || member.IsBusy || member.IsOwner)
            return;

        member.IsBusy = true;
        member.ErrorMessage = string.Empty;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = BuildMembersClient();
            await client.RemoveMemberAsync(token, _projectId, member.UserId);
            Members.Remove(member);
        }
        catch (Exception ex)
        {
            member.IsBusy = false;
            member.ErrorMessage = FriendlyMessage(ex);
        }
    }

    [RelayCommand]
    private async Task CancelInviteAsync(SentInviteVm? invite)
    {
        if (invite is null || invite.IsBusy)
            return;

        invite.IsBusy = true;
        invite.ErrorMessage = string.Empty;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = BuildInvitesClient();
            await client.CancelInviteAsync(token, invite.Id);
            SentInvites.Remove(invite);
        }
        catch (Exception ex)
        {
            invite.IsBusy = false;
            invite.ErrorMessage = FriendlyMessage(ex);
        }
    }
}

/// <summary>
/// Fila bindeable de <see cref="ShareViewModel.Members"/> (wrapea <see cref="MemberDto"/>).
/// <see cref="IsOwner"/> gatea la UI para que el dueño se muestre SIN controles de cambio de rol
/// ni de "Quitar" -- comodidad, no seguridad (ver header de <see cref="ShareViewModel"/>).
/// </summary>
public partial class MemberVm : ObservableObject
{
    public string UserId { get; }
    public string Email { get; }
    public string Role { get; }
    public bool IsOwner => Role == "owner";

    [ObservableProperty]
    private string _selectedRole;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public MemberVm(MemberDto dto)
    {
        UserId = dto.UserId;
        Email = dto.Email;
        Role = dto.Role;
        _selectedRole = dto.Role;
    }
}

/// <summary>Fila bindeable de <see cref="ShareViewModel.SentInvites"/> (wrapea <see cref="InviteDto"/>
/// -- el contrato de invitaciones "enviadas" no manda el email del invitado, solo su rol y fecha,
/// así que la fila se identifica por esos datos en vez del id crudo.</summary>
public partial class SentInviteVm : ObservableObject
{
    public string Id { get; }
    public string Role { get; }
    public DateTimeOffset CreatedAt { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public SentInviteVm(InviteDto dto)
    {
        Id = dto.Id;
        Role = dto.Role;
        CreatedAt = dto.CreatedAt;
    }
}
