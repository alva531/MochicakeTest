Shader "Custom/SnowDeform"
{
    Properties
    {
        [Header(Snow Appearance)]
        _SnowColor          ("Snow Color",              Color)        = (0.90, 0.95, 1.00, 1)
        _CompressedColor    ("Compressed Snow Color",   Color)        = (0.70, 0.78, 0.88, 1)
        _InteriorColor      ("Interior Snow Color",     Color)        = (0.75, 0.82, 0.92, 1)
        _SnowTex            ("Snow Texture (Granularity)", 2D)        = "white" {}
        _SnowTiling         ("Snow Texture Tiling",     Float)        = 8.0
        _SnowTextureStrength("Snow Texture Strength",   Range(0,1))   = 0.15
        _NormalStrength     ("Normal Exaggeration",     Range(0,20))  = 6

        [Header(Trail)]
        _TrailTex           ("Trail Render Texture",    2D)           = "black" {}

        [Header(Displacement)]
        _SnowHeight         ("Snow Height",             Float)        = 0.12
        _DeformDepth        ("Deform Depth",            Float)        = 0.28

        [Header(Lighting)]
        _AmbientColor       ("Ambient Color",           Color)        = (0.15, 0.18, 0.25, 1)
        _RimColor           ("Rim Light Color",         Color)        = (0.60, 0.75, 1.00, 1)
        _RimPower           ("Rim Power",               Range(0,8))   = 3.0

        [Header(Tessellation)]
        _TessFactor         ("Tessellation Factor",     Range(1,32))  = 14
        _TessMaxDistance    ("Tess Max Distance",       Float)        = 20
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
            #pragma vertex   vert
            #pragma hull     hull
            #pragma domain   domain
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ── Textures ──────────────────────────────────────────────────────

            TEXTURE2D(_TrailTex);  SAMPLER(sampler_TrailTex);
            TEXTURE2D(_SnowTex);   SAMPLER(sampler_SnowTex);

            // ── Constant Buffer ───────────────────────────────────────────────

            CBUFFER_START(UnityPerMaterial)
                float4 _SnowColor;
                float4 _CompressedColor;
                float4 _InteriorColor;
                float4 _AmbientColor;
                float4 _RimColor;
                float4 _TrailTex_ST;
                float4 _SnowTex_ST;
                float  _SnowTiling;
                float  _SnowTextureStrength;
                float  _NormalStrength;
                float  _SnowHeight;
                float  _DeformDepth;
                float  _RimPower;
                float  _TessFactor;
                float  _TessMaxDistance;
            CBUFFER_END

            // ── Structs ───────────────────────────────────────────────────────

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct ControlPoint
            {
                float4 positionOS : INTERNALTESSPOS;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct TessFactors
            {
                float edge[3] : SV_TessFactor;
                float inside  : SV_InsideTessFactor;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float2 uv          : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float3 viewDirWS   : TEXCOORD3;
                float  fogFactor   : TEXCOORD4;
            };

            // ── Vertex (pass-through) ─────────────────────────────────────────

            ControlPoint vert(Attributes IN)
            {
                ControlPoint OUT;
                OUT.positionOS = IN.positionOS;
                OUT.normalOS   = IN.normalOS;
                OUT.uv         = IN.uv;
                return OUT;
            }

            // ── Hull ─────────────────────────────────────────────────────────

            TessFactors PatchConstant(InputPatch<ControlPoint, 3> patch)
            {
                float3 center = TransformObjectToWorld(
                    (patch[0].positionOS + patch[1].positionOS + patch[2].positionOS).xyz / 3.0
                );
                float dist   = distance(center, _WorldSpaceCameraPos);
                float factor = max(1.0, _TessFactor * (1.0 - saturate(dist / _TessMaxDistance)));

                TessFactors f;
                f.edge[0] = factor; f.edge[1] = factor; f.edge[2] = factor;
                f.inside  = factor;
                return f;
            }

            [domain("tri")]
            [outputcontrolpoints(3)]
            [outputtopology("triangle_cw")]
            [partitioning("fractional_odd")]
            [patchconstantfunc("PatchConstant")]
            ControlPoint hull(InputPatch<ControlPoint, 3> patch, uint id : SV_OutputControlPointID)
            {
                return patch[id];
            }

            // ── Domain (displacement happens here) ────────────────────────────

            [domain("tri")]
            Varyings domain(TessFactors factors, OutputPatch<ControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float4 posOS  = patch[0].positionOS * bary.x + patch[1].positionOS * bary.y + patch[2].positionOS * bary.z;
                float3 normOS = patch[0].normalOS   * bary.x + patch[1].normalOS   * bary.y + patch[2].normalOS   * bary.z;
                float2 uv     = patch[0].uv          * bary.x + patch[1].uv          * bary.y + patch[2].uv          * bary.z;

                float  trail = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv, 0).r;
                float3 norm  = normalize(normOS);

                posOS.xyz += norm * _SnowHeight;
                posOS.xyz -= norm * (trail * _DeformDepth);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(posOS.xyz);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(normOS);

                Varyings OUT;
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS   = normInputs.normalWS;
                OUT.viewDirWS  = GetWorldSpaceViewDir(posInputs.positionWS);
                OUT.uv         = uv;
                OUT.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            // ── Fragment ─────────────────────────────────────────────────────

            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                // Base normal from mesh, stable across triangles
                float3 baseNormal = normalize(IN.normalWS);
                if (!isFrontFace) baseNormal = -baseNormal;

                // Trail gradient — perturbs normal to show groove walls
                float eps = 1.0 / 512.0;
                float tL  = SAMPLE_TEXTURE2D(_TrailTex, sampler_TrailTex, IN.uv - float2(eps, 0)).r;
                float tR  = SAMPLE_TEXTURE2D(_TrailTex, sampler_TrailTex, IN.uv + float2(eps, 0)).r;
                float tD  = SAMPLE_TEXTURE2D(_TrailTex, sampler_TrailTex, IN.uv - float2(0, eps)).r;
                float tU  = SAMPLE_TEXTURE2D(_TrailTex, sampler_TrailTex, IN.uv + float2(0, eps)).r;

                // Reconstruct tangent frame from UV and position derivatives
                float2 duv_dx      = ddx(IN.uv);
                float2 duv_dy      = ddy(IN.uv);
                float3 dp_dx       = ddx(IN.positionWS);
                float3 dp_dy       = ddy(IN.positionWS);
                float3 tangentWS   = normalize(dp_dx * duv_dy.y - dp_dy * duv_dx.y);
                float3 bitangentWS = normalize(dp_dy * duv_dx.x - dp_dx * duv_dy.x);

                float  dU = (tR - tL) * _NormalStrength;
                float  dV = (tU - tD) * _NormalStrength;
                float3 perturbedNormal = normalize(baseNormal - dU * tangentWS - dV * bitangentWS);

                // ── Lighting ──────────────────────────────────────────────────
                Light  mainLight = GetMainLight();
                float  NdotL     = saturate(dot(perturbedNormal, mainLight.direction));
                float3 lighting  = mainLight.color * NdotL + _AmbientColor.rgb;

                float3 viewDir  = normalize(IN.viewDirWS);
                float  rim      = pow(1.0 - saturate(dot(perturbedNormal, viewDir)), _RimPower);
                float3 rimLight = _RimColor.rgb * rim * 0.2;

                // Interior back faces (solid snow fill)
                if (!isFrontFace)
                    return float4(_InteriorColor.rgb * lighting + rimLight, 1.0);

                float trail = SAMPLE_TEXTURE2D(_TrailTex, sampler_TrailTex, IN.uv).r;

                // Fake AO — darkens groove bottom to simulate self-shadowing
                float ao = lerp(1.0, 0.45, trail);

                // Specular — compressed snow is icier and shinier
                float3 halfDir  = normalize(mainLight.direction + viewDir);
                float  spec     = pow(saturate(dot(perturbedNormal, halfDir)), 48.0);
                float3 specular = mainLight.color * spec * trail * 0.25;

                // Snow surface granularity texture
                float2 snowUV    = IN.uv * _SnowTiling;
                float  snowGrain = SAMPLE_TEXTURE2D(_SnowTex, sampler_SnowTex, snowUV).r;
                float  grain     = lerp(1.0, snowGrain, _SnowTextureStrength);

                float3 color      = lerp(_SnowColor.rgb, _CompressedColor.rgb, trail) * grain;
                float3 finalColor = color * lighting * ao + rimLight + specular;

                finalColor = MixFog(finalColor, IN.fogFactor);
                return float4(finalColor, 1.0);
            }
            ENDHLSL
        }

        // ── Shadow Caster ─────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex   shadowVert
            #pragma hull     shadowHull
            #pragma domain   shadowDomain
            #pragma fragment shadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_TrailTex); SAMPLER(sampler_TrailTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _TrailTex_ST;
                float4 _SnowTex_ST;
                float4 _SnowColor;
                float4 _CompressedColor;
                float4 _InteriorColor;
                float4 _AmbientColor;
                float4 _RimColor;
                float  _SnowTiling;
                float  _SnowTextureStrength;
                float  _NormalStrength;
                float  _SnowHeight;
                float  _DeformDepth;
                float  _RimPower;
                float  _TessFactor;
                float  _TessMaxDistance;
            CBUFFER_END

            struct Attributes   { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct ControlPoint { float4 positionOS : INTERNALTESSPOS; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct TessFactors  { float edge[3] : SV_TessFactor; float inside : SV_InsideTessFactor; };
            struct Varyings     { float4 positionCS : SV_POSITION; };

            ControlPoint shadowVert(Attributes IN)
            {
                ControlPoint OUT;
                OUT.positionOS = IN.positionOS;
                OUT.normalOS   = IN.normalOS;
                OUT.uv         = IN.uv;
                return OUT;
            }

            TessFactors ShadowPatch(InputPatch<ControlPoint, 3> patch)
            {
                float3 center = TransformObjectToWorld(
                    (patch[0].positionOS + patch[1].positionOS + patch[2].positionOS).xyz / 3.0
                );
                float dist   = distance(center, _WorldSpaceCameraPos);
                float factor = max(1.0, _TessFactor * (1.0 - saturate(dist / _TessMaxDistance)));
                TessFactors f;
                f.edge[0] = factor; f.edge[1] = factor; f.edge[2] = factor;
                f.inside  = factor;
                return f;
            }

            [domain("tri")]
            [outputcontrolpoints(3)]
            [outputtopology("triangle_cw")]
            [partitioning("fractional_odd")]
            [patchconstantfunc("ShadowPatch")]
            ControlPoint shadowHull(InputPatch<ControlPoint, 3> patch, uint id : SV_OutputControlPointID)
            {
                return patch[id];
            }

            [domain("tri")]
            Varyings shadowDomain(TessFactors factors, OutputPatch<ControlPoint, 3> patch,
                                  float3 bary : SV_DomainLocation)
            {
                float4 posOS  = patch[0].positionOS * bary.x + patch[1].positionOS * bary.y + patch[2].positionOS * bary.z;
                float3 normOS = patch[0].normalOS   * bary.x + patch[1].normalOS   * bary.y + patch[2].normalOS   * bary.z;
                float2 uv     = patch[0].uv          * bary.x + patch[1].uv          * bary.y + patch[2].uv          * bary.z;

                float  trail = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv, 0).r;
                float3 norm  = normalize(normOS);
                posOS.xyz   += norm * _SnowHeight;
                posOS.xyz   -= norm * (trail * _DeformDepth);

                float3 worldPos    = TransformObjectToWorld(posOS.xyz);
                float3 worldNormal = TransformObjectToWorldNormal(normOS);

                float3 lightDir   = normalize(_MainLightPosition.xyz);
                float  NdotL      = saturate(dot(worldNormal, lightDir));
                float3 biasOffset = worldNormal * (1.0 - NdotL) * 0.02;
                worldPos         += biasOffset;

                Varyings OUT;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                return OUT;
            }

            float4 shadowFrag(Varyings IN) : SV_Target { return 0; }

            ENDHLSL
        }
    }
}
