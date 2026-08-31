function base64urlDecode(str: string): ArrayBuffer {
  const base64 = str.replace(/-/g, '+').replace(/_/g, '/')
  const pad = base64.length % 4
  const padded = pad ? base64 + '='.repeat(4 - pad) : base64
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes.buffer
}

function base64urlEncode(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  let binary = ''
  for (const b of bytes) binary += String.fromCharCode(b)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

interface AssertionOptionsJson {
  challenge: string
  timeout?: number
  rpId?: string
  allowCredentials?: Array<{ type: string; id: string; transports?: string[] }>
  userVerification?: string
  extensions?: Record<string, unknown>
}

export async function startAuthentication(optionsJson: string): Promise<string> {
  const options: AssertionOptionsJson = JSON.parse(optionsJson)

  const publicKey: PublicKeyCredentialRequestOptions = {
    challenge: base64urlDecode(options.challenge),
    timeout: options.timeout,
    rpId: options.rpId,
    userVerification: (options.userVerification as UserVerificationRequirement) ?? 'required',
    allowCredentials: options.allowCredentials?.map((c) => ({
      type: c.type as PublicKeyCredentialType,
      id: base64urlDecode(c.id),
      transports: c.transports as AuthenticatorTransport[] | undefined,
    })),
    extensions: options.extensions as AuthenticationExtensionsClientInputs,
  }

  const credential = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null
  if (!credential) throw new Error('No credential returned')

  const response = credential.response as AuthenticatorAssertionResponse

  return JSON.stringify({
    id: credential.id,
    rawId: base64urlEncode(credential.rawId),
    type: credential.type,
    response: {
      authenticatorData: base64urlEncode(response.authenticatorData),
      clientDataJSON: base64urlEncode(response.clientDataJSON),
      signature: base64urlEncode(response.signature),
      userHandle: response.userHandle ? base64urlEncode(response.userHandle) : null,
    },
  })
}
