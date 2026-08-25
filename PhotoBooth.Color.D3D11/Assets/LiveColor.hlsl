Texture2D FrameTexture : register(t0);
Texture3D LutTexture : register(t1);
SamplerState FrameSampler : register(s0);
SamplerState LutSampler : register(s1);
struct Output { float4 Position : SV_POSITION; float2 Uv : TEXCOORD0; };
Output VSMain(uint id : SV_VertexID) { Output o; o.Uv=float2((id<<1)&2,id&2); o.Position=float4(o.Uv*float2(2,-2)+float2(-1,1),0,1); return o; }
float4 PSMain(float4 position : SV_POSITION,float2 uv : TEXCOORD0) : SV_TARGET { float4 c=FrameTexture.Sample(FrameSampler,uv); return float4(LutTexture.Sample(LutSampler,saturate(c.rgb)).rgb,c.a); }
