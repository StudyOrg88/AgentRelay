# Device Agent Installation and Preflight

This guide prepares a registered Windows or Ubuntu host for AgentRelay. Registry values are configuration, not credentials. Do not place passwords, private keys, tokens, or prompts in SSH arguments.

## Windows target

Run elevated PowerShell to install and enable OpenSSH Server, then ensure the firewall permits only the intended controller network:

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType Automatic
Get-NetFirewallRule -Name OpenSSH-Server-In-TCP
```

On the controller, obtain the server host-key fingerprint through a trusted out-of-band channel and compare it with the target's `ssh-keygen -lf` output before installing the public host key into the controller user's `known_hosts`. Do not accept a first-use prompt automatically.

## Ubuntu target

```bash
sudo apt-get update
sudo apt-get install -y openssh-server git
sudo systemctl enable --now ssh
sudo sshd -T | grep -E '^(port|pubkeyauthentication|passwordauthentication) '
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Restrict inbound SSH to the controller network and configure public-key authentication according to local policy. Verify the host-key fingerprint out of band, then pin that key in the controller user's `known_hosts` file. AgentRelay sets `StrictHostKeyChecking=yes`; unknown or changed host keys fail closed.

## Both platforms

Install the .NET runtime required by the published AgentRelay Agent (or publish self-contained), and install the Agent at a stable executable path available to the SSH account. Confirm `agentrelay-agent --help` or the deployment's supported operation invocation works without interactive prompts. Do not place a variable command or executable in task payloads.

The Controller's `agentExecutable` constructor/configuration value is trusted deployment configuration owned by the controller operator. It must be set in the controller deployment, never loaded from an Issue, task payload, or device registry. SSH transport requires this value to be one shell-safe executable basename (for example `agentrelay-agent`) available on the remote account's `PATH`; paths, arguments, whitespace, and shell metacharacters are rejected. The transport appends only one allowlisted operation and never accepts command text from a device.

Install Git and Codex CLI for the SSH account. Log in to Codex CLI interactively as that account and verify its existing least-privilege role credentials; do not send credentials through SSH stdin or registry files. Confirm the account can run `codex --version` and `git --version` in a non-interactive SSH session.

Register only approved absolute repository/workspace paths. Preflight should verify the path exists, is writable by the agent account where needed, and Git can inspect the repository and create an isolated worktree. The remote agent must enforce its own repository allowlist; registry selection alone is not authorization.

From the controller, validate the registered SSH alias, user, and port and test a non-interactive connection only after host-key pinning:

```bash
ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -p 22 registered-user@registered-host 'agentrelay-agent preflight' < request.json
```

Normal AgentRelay transport passes the operation and JSON request over its fixed protocol; never substitute issue text or arbitrary command strings into the remote command. Task 5's Ubuntu/real OpenSSH end-to-end behavior remains unverified and must be covered by follow-up CI before relying on remote dispatch in production.
