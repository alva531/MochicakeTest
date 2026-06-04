// VARIANTE A — Minimalista
// Trail azul-gris limpio, sin granularidad, muy legible
Shader "Custom/SnowDeform_A"
{
    Properties
    {
        _SnowColor       ("Snow Color",          Color)        = (0.93, 0.96, 1.00, 1)
        _TrailColor      ("Trail Color",         Color)        = (0.50, 0.68, 0.80, 1)
        _TrailTex        ("Trail RT",            2D)           = "black" {}
        _SnowHeight      ("Snow Height",         Float)        = 0.15
        _DeformDepth     ("Deform Depth",        Float)        = 0.35
        _NormalStrength  ("Normal Strength",     Range(0,20))  = 8
        _AmbientColor    ("Ambient",             Color)        = (0.18, 0.22, 0.30, 1)
        _TessFactor      ("Tess Factor",         Range(1,32))  = 16
        _TessMaxDistance ("Tess Max Distance",   Float)        = 25
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex vert
            #pragma hull   hull
            #pragma domain domain
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_TrailTex); SAMPLER(sampler_TrailTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _SnowColor; float4 _TrailColor; float4 _AmbientColor;
                float4 _TrailTex_ST;
                float _SnowHeight; float _DeformDepth; float _NormalStrength;
                float _TessFactor; float _TessMaxDistance;
            CBUFFER_END

            struct Attributes  { float4 pos : POSITION; float3 nor : NORMAL; float2 uv : TEXCOORD0; };
            struct CP          { float4 pos : INTERNALTESSPOS; float3 nor : NORMAL; float2 uv : TEXCOORD0; };
            struct TF          { float edge[3] : SV_TessFactor; float inside : SV_InsideTessFactor; };
            struct Varyings    { float4 cs : SV_POSITION; float3 nWS : TEXCOORD0; float2 uv : TEXCOORD1;
                                 float3 pWS : TEXCOORD2; float3 vWS : TEXCOORD3; float fog : TEXCOORD4; };

            CP vert(Attributes i) { CP o; o.pos=i.pos; o.nor=i.nor; o.uv=i.uv; return o; }

            TF PC(InputPatch<CP,3> p) {
                float3 c = TransformObjectToWorld((p[0].pos+p[1].pos+p[2].pos).xyz/3);
                float f = max(1, _TessFactor*(1-saturate(distance(c,_WorldSpaceCameraPos)/_TessMaxDistance)));
                TF o; o.edge[0]=o.edge[1]=o.edge[2]=o.inside=f; return o;
            }
            [domain("tri")][outputcontrolpoints(3)][outputtopology("triangle_cw")]
            [partitioning("fractional_odd")][patchconstantfunc("PC")]
            CP hull(InputPatch<CP,3> p, uint id:SV_OutputControlPointID) { return p[id]; }

            [domain("tri")]
            Varyings domain(TF tf, OutputPatch<CP,3> p, float3 b:SV_DomainLocation) {
                float4 pos = p[0].pos*b.x+p[1].pos*b.y+p[2].pos*b.z;
                float3 nor = p[0].nor*b.x+p[1].nor*b.y+p[2].nor*b.z;
                float2 uv  = p[0].uv*b.x+p[1].uv*b.y+p[2].uv*b.z;
                float t = SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv,0).r;
                float3 n = normalize(nor);
                pos.xyz += n*_SnowHeight - n*(t*_DeformDepth);
                VertexPositionInputs pi = GetVertexPositionInputs(pos.xyz);
                VertexNormalInputs   ni = GetVertexNormalInputs(nor);
                Varyings o; o.cs=pi.positionCS; o.pWS=pi.positionWS;
                o.nWS=ni.normalWS; o.vWS=GetWorldSpaceViewDir(pi.positionWS);
                o.uv=uv; o.fog=ComputeFogFactor(pi.positionCS.z); return o;
            }

            float4 frag(Varyings i, bool ff:SV_IsFrontFace) : SV_Target {
                float3 N = normalize(i.nWS); if(!ff) N=-N;
                float eps=1./512.;
                float tL=SAMPLE_TEXTURE2D(_TrailTex,sampler_TrailTex,i.uv-float2(eps,0)).r;
                float tR=SAMPLE_TEXTURE2D(_TrailTex,sampler_TrailTex,i.uv+float2(eps,0)).r;
                float tD=SAMPLE_TEXTURE2D(_TrailTex,sampler_TrailTex,i.uv-float2(0,eps)).r;
                float tU=SAMPLE_TEXTURE2D(_TrailTex,sampler_TrailTex,i.uv+float2(0,eps)).r;
                float3 T=normalize(ddx(i.pWS)*ddy(i.uv).y-ddy(i.pWS)*ddx(i.uv).y);
                float3 B=normalize(ddy(i.pWS)*ddx(i.uv).x-ddx(i.pWS)*ddy(i.uv).x);
                N = normalize(N-(tR-tL)*_NormalStrength*T-(tU-tD)*_NormalStrength*B);
                Light L=GetMainLight();
                float ndl=saturate(dot(N,L.direction));
                float3 lit=L.color*ndl+_AmbientColor.rgb;
                float trail=SAMPLE_TEXTURE2D(_TrailTex,sampler_TrailTex,i.uv).r;
                if(!ff) return float4(_TrailColor.rgb*0.8*lit,1);
                float ao=lerp(1,0.5,trail);
                float3 col=lerp(_SnowColor.rgb,_TrailColor.rgb,trail);
                return float4(MixFog(col*lit*ao,i.fog),1);
            }
            ENDHLSL
        }
    }
}
