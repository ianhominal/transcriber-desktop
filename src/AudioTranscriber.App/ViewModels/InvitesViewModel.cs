using System.Collections.ObjectModel;
using System.Net.Http;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// ViewModel de "Invitaciones pendientes" (Team Sharing slice 1b, Phase 16, design ADR-13
/// "paridad de invitaciones pendientes en los dos clientes"): ver y resolver (aceptar/rechazar)
/// las invitaciones que el usuario recibió, contra EL MISMO contrato de servidor que ya usa la web
/// (<see cref="InvitesApiClient"/>, Phase 9.5/9.3). Cero lógica de permisos acá (regla dura del
/// pedido): el desktop solo pide/muestra lo que el servidor manda y deja que el servidor
/// acepte/rechace cada acción -- no hay ningún <c>if (role == ...)</c> en este archivo.
/// </summary>
public partial class InvitesViewModel : ObservableObject
{
    public ObservableCollection<InviteVm> Invites { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private static async Task<string> GetAccessTokenOrThrowAsync()
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Iniciá sesión desde 'Sincronización' para ver tus invitaciones.");
        return token;
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    /// <summary>Trae las invitaciones `pending` recibidas (ver <see cref="InvitesApiClient.GetPendingInvitesAsync"/>).
    /// Se dispara al abrir <c>InvitesWindow</c> y con el botón "Actualizar".</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new InvitesApiClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            var invites = await client.GetPendingInvitesAsync(token);

            Invites.Clear();
            foreach (var invite in invites)
                Invites.Add(new InviteVm(invite));
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

    [RelayCommand]
    private async Task AcceptAsync(InviteVm? invite) => await RespondAsync(invite, "accept");

    [RelayCommand]
    private async Task RejectAsync(InviteVm? invite) => await RespondAsync(invite, "reject");

    /// <summary>
    /// Manda la acción al servidor (<see cref="InvitesApiClient.RespondInviteAsync"/>) y, si salió
    /// bien, saca la fila de <see cref="Invites"/> -- el servidor ya la resolvió (`accepted` crea
    /// la membresía vía la RPC transaccional, `rejected` no crea nada, spec "Aceptar crea la
    /// membresía; rechazar no la crea"), así que dejó de estar `pending` y no corresponde seguir
    /// mostrándola acá sin esperar un refresh manual.
    /// </summary>
    private async Task RespondAsync(InviteVm? invite, string action)
    {
        if (invite is null || invite.IsBusy)
            return;

        invite.IsBusy = true;
        invite.ErrorMessage = string.Empty;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new InvitesApiClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            await client.RespondInviteAsync(token, invite.Id, action);
            Invites.Remove(invite);
        }
        catch (Exception ex)
        {
            invite.IsBusy = false;
            invite.ErrorMessage = FriendlyMessage(ex);
        }
    }
}

/// <summary>
/// Fila bindeable de <see cref="InvitesViewModel.Invites"/> (wrapea <see cref="InviteDto"/>). El
/// rol se muestra TAL CUAL lo manda el servidor, sin traducirlo a permisos -- esa decisión es
/// 100% del backend (ver header de <see cref="InvitesViewModel"/>). <see cref="DisplayProjectName"/>
/// (Phase 17): <c>GET /api/invites</c> ahora manda <see cref="InviteDto.ProjectName"/> además del
/// id crudo, así que la ventana deja de mostrar un UUID -- se conserva un fallback al id por si
/// algún build viejo del servidor todavía no lo manda.
/// </summary>
public partial class InviteVm : ObservableObject
{
    public string Id { get; }
    public string ProjectId { get; }
    public string Role { get; }
    public string DisplayProjectName { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public InviteVm(InviteDto dto)
    {
        Id = dto.Id;
        ProjectId = dto.ProjectId;
        Role = dto.Role;
        DisplayProjectName = string.IsNullOrWhiteSpace(dto.ProjectName) ? dto.ProjectId : dto.ProjectName;
    }
}
