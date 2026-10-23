# Permissions

Permissions decide what each person, script and agent install can do, and let each tenant run itself.

## Who receives them

Permissions are allowed or denied to a user, a user group, a personal access token or a service account. Personal
access tokens and service accounts let scripts use the API with their own, limited permissions, so a script never
needs a person's password.

## Where they apply

A permission is granted at server, tenant, device group or device scope. Device permissions granted on a device group
reach every device in it, and a device can be in several groups.

## A deny always wins

Wherever it comes from, a deny always wins: a user allowed to read every device of the tenant but denied one device
doesn't see that device, even through a group that allows it. Presets such as Tenant Administrator and Self Service
group the usual permissions.

## Where they are checked

Every API route and dashboard hub method checks its permission, and the device list, each device and live updates
agree on what a user may read. Every permission change is kept in an authorization log, per tenant and for the server,
for `AuthorizationLogs:RetentionDays`.

Tenants manage their users, invites, user groups, device groups and settings; server administrators manage tenants
and a server-wide alert. Every route is tested with and without its permission and from another tenant.
