# Authentication and Authorization Roadmap

Current completed foundation:

- Auth database models for Accounts, Users, Roles, Permissions, Account Roles, Role Permissions, Sessions, and Audit Logs.
- Account creation endpoint.
- Active-role lookup endpoint.
- Login endpoint with password hashing verification.
- Access-token and refresh-token generation.
- Session creation and last-login timestamp updates.

## Remaining Work

- [x] 1. Add access-token authentication middleware.
- [x] 2. Add permission-based authorization to protected API endpoints.
- [ ] 3. Add a refresh-token endpoint.
- [ ] 4. Add a logout endpoint and session revocation.
- [ ] 5. Protect account creation so only an authorized account administrator can call it.
- [ ] 6. Add Super Admin account-management UI support and endpoints:
  - List all Accounts with their User profile, status, Roles, creation time, and last-login time.
  - Search and filter Accounts by email, name, status, and Role.
  - View one Account and its complete User, Role, Permission, and Session summary.
  - Create Accounts and User profiles.
  - Edit Account and User profile information.
  - Assign and remove Account Roles.
  - Enable and disable Accounts.
  - Delete Accounts while preserving User identity and Audit Log history.
- [ ] 7. Add password-management flows: change password, administrator reset, forgotten password, and session invalidation after password changes.
- [ ] 8. Add Super Admin Role and Permission management UI support and endpoints:
  - List, view, create, edit, enable, disable, and delete Roles.
  - List, view, create, edit, enable, disable, and delete Permissions.
  - Assign Permissions to Roles and remove Permissions from Roles.
  - Show which Accounts use each Role and which Roles use each Permission.
- [ ] 9. Integrate Audit Log writes into important create, update, delete, status-change, login, and permission operations.
- [ ] 10. Add transactional account-deletion cleanup for Account Roles, Sessions, User account links, and Audit Log account links while preserving User identity and Audit Log history.
- [ ] 11. Add login-security controls: rate limiting, failed-login protection, expired-session cleanup, and optional multi-factor authentication.

Items should be implemented in order unless a later requirement changes the priority.
