Shader "Custom/SnowDeform_URP"
{
    Properties
    {
        [Header(Snow)]
        _SnowColor          ("Snow Color",              Color)       = (0.90, 0.95, 1.00, 1)
        _CompressedColor    ("Compressed Color",        Color)       = (0.55, 0.75, 0.90, 1)
        _SnowSmoothness     ("Snow Smoothness",         Range(0,1))  = 0.10
        _IceSmoothness      ("Ice Smoothness",          Range(0,1))  = 0.55
        _SnowTex            ("Snow Texture",            2D)          = "white" {}
        _SnowTiling         ("Snow Tiling",             Float)       = 10.0
        _SnowTexStrength    ("Snow Texture Strength",   Range(0,1))  = 0.12

        [Header(Trail)]
        _TrailTex           ("Trail Render Texture",    2D)          = "black" {}

        [Header(Displacement)]
        _SnowHeight         ("Snow Height (groove depth)", Float)    = 0.20
        _BermHeight         ("Berm Ridge Height",       Float)       = 0.06
        _BermWidth          ("Berm Falloff Width",      Float)       = 0.03
        _NormalStrength     ("Normal Strength",         Range(0,20)) = 10.0

        [Header(Tessellation)]
        _TessFactor         ("Tessellation Factor",     Range(1,64)) = 24
        _TessMaxDistance    ("Tess Max Distance",       Float)       = 25
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex   vert
            #pragma hull     hull
            #pragma domain   domain
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

            // ── Textures ──────────────────────────────────────────────────────
            TEXTURE2D(_TrailTex); SAMPLER(sampler_TrailTex);
            TEXTURE2D(_SnowTex);  SAMPLER(sampler_SnowTex);

            // ── CBUFFER ───────────────────────────────────────────────────────
            CBUFFER_START(UnityPerMaterial)
                float4 _SnowColor;
                float4 _CompressedColor;
                float4 _TrailTex_ST;
                float4 _SnowTex_ST;
                float  _SnowSmoothness;
                float  _IceSmoothness;
                float  _SnowTiling;
                float  _SnowTexStrength;
                float  _SnowHeight;
                float  _BermHeight;
                float  _BermWidth;
                float  _NormalStrength;
                float  _TessFactor;
                float  _TessMaxDistance;
            CBUFFER_END

            // ── Structs ───────────────────────────────────────────────────────
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ControlPoint
            {
                float4 positionOS : INTERNALTESSPOS;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct TessFactors
            {
                float edge[3] : SV_TessFactor;
                float inside  : SV_InsideTessFactor;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
                float3 viewDirWS   : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                float  fogFactor   : TEXCOORD5;
                // Pack groove depth + berm into one interpolator for the frag shader
                float2 trailData   : TEXCOORD6; // x = groove, y = berm influence
            };

            // ── Helpers ───────────────────────────────────────────────────────

            // Samples the trail texture at UV and returns the displacement height.
            // Groove sinks down by _SnowHeight where trail==1.
            // A small berm ridge is raised just outside the groove edge.
            float ComputeDisplacement(float2 uv, out float groove, out float berm)
            {
                float trail = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv, 0).r;

                // Soften the groove edge so the wall reads as a slope, not a cliff
                groove = smoothstep(0.0, 0.5, trail);

                // Berm: sample a slightly wider kernel; wherever the edge gradient
                // is strong (outside > 0, center = 0) we push up.
                float2 offU = float2(_BermWidth, 0);
                float2 offV = float2(0, _BermWidth);
                float tL = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv - offU, 0).r;
                float tR = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv + offU, 0).r;
                float tD = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv - offV, 0).r;
                float tU = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv + offV, 0).r;
                float neighborMax = max(max(tL, tR), max(tD, tU));

                // Berm is only where there IS neighbor trail but THIS texel has no trail
                berm = saturate(neighborMax - trail) * (1.0 - trail);
                berm = smoothstep(0.0, 1.0, berm);

                return -groove * _SnowHeight + berm * _BermHeight;
            }

            // Finite-difference normal from the trail heightfield.
            // Called in the domain shader so normals match actual geometry.
            float3 ComputeTrailNormal(float2 uv, float3 normOS)
            {
                float eps = 1.0 / 512.0; // match your RT size

                float2 offU = float2(eps, 0);
                float2 offV = float2(0, eps);

                // Sample height at 4 neighbours (groove only for normal, berm is smooth enough)
                float hL = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv - offU, 0).r * _SnowHeight;
                float hR = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv + offU, 0).r * _SnowHeight;
                float hD = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv - offV, 0).r * _SnowHeight;
                float hU = SAMPLE_TEXTURE2D_LOD(_TrailTex, sampler_TrailTex, uv + offV, 0).r * _SnowHeight;

                // Build a local tangent-space normal from height differences
                float3 tn = normalize(float3(
                    (hL - hR) * _NormalStrength,
                    (hD - hU) * _NormalStrength,
                    1.0
                ));

                // Rotate into object space using the surface normal as "up"
                float3 n   = normalize(normOS);
                float3 t   = normalize(cross(n, float3(0,0,1) + n * 0.001));
                float3 b   = cross(n, t);
                return normalize(t * tn.x + b * tn.y + n * tn.z);
            }

            // ── Vertex ────────────────────────────────────────────────────────
            ControlPoint vert(Attributes IN)
            {
                ControlPoint OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionOS = IN.positionOS;
                OUT.normalOS   = IN.normalOS;
                OUT.uv         = IN.uv;
                return OUT;
            }

            // ── Hull ─────────────────────────────────────────────────────────
            TessFactors PatchConstant(InputPatch<ControlPoint, 3> patch)
            {
                float3 center = TransformObjectToWorld(
                    (patch[0].positionOS + patch[1].positionOS + patch[2].positionOS).xyz / 3.0);
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

            // ── Domain ───────────────────────────────────────────────────────
            [domain("tri")]
            Varyings domain(TessFactors tf, OutputPatch<ControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float4 posOS  = patch[0].positionOS * bary.x + patch[1].positionOS * bary.y + patch[2].positionOS * bary.z;
                float3 normOS = patch[0].normalOS   * bary.x + patch[1].normalOS   * bary.y + patch[2].normalOS   * bary.z;
                float2 uv     = patch[0].uv          * bary.x + patch[1].uv         * bary.y + patch[2].uv         * bary.z;

                float groove, berm;
                float disp = ComputeDisplacement(uv, groove, berm);

                float3 norm = normalize(normOS);
                posOS.xyz  += norm * disp;

                // Normals computed HERE in domain (geometry-accurate)
                float3 displNorm = ComputeTrailNormal(uv, normOS);

                VertexPositionInputs posInputs = GetVertexPositionInputs(posOS.xyz);
                VertexNormalInputs   norInputs = GetVertexNormalInputs(displNorm);

                Varyings OUT;
                OUT.positionCS  = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = norInputs.normalWS;
                OUT.uv          = uv;
                OUT.viewDirWS   = GetWorldSpaceViewDir(posInputs.positionWS);
                OUT.shadowCoord = GetShadowCoord(posInputs);
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                OUT.trailData   = float2(groove, berm);
                return OUT;
            }

            // ── Fragment ──────────────────────────────────────────────────────
            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                float3 N      = normalize(IN.normalWS);
                if (!isFrontFace) N = -N;

                float groove = IN.trailData.x;
                float berm   = IN.trailData.y;

                // Snow texture grain
                float grain = lerp(1.0,
                    SAMPLE_TEXTURE2D(_SnowTex, sampler_SnowTex, IN.uv * _SnowTiling).r,
                    _SnowTexStrength);

                // Albedo: snow → compressed ice in the groove
                // Berm gets slightly darker compressed snow (recently displaced)
                float3 bermColor  = lerp(_SnowColor.rgb, _CompressedColor.rgb, 0.4);
                float3 baseAlbedo = lerp(_SnowColor.rgb, _CompressedColor.rgb, groove);
                       baseAlbedo = lerp(baseAlbedo, bermColor, berm * 0.6);
                float3 albedo     = baseAlbedo * grain;

                float smoothness = lerp(_SnowSmoothness, _IceSmoothness, groove);

                // AO: deepest at center of groove, fades toward edges and berm
                float ao = lerp(1.0, 0.35, groove * (1.0 - berm * 0.5));

                // Inner back faces (walls of the groove seen from below)
                if (!isFrontFace)
                {
                    albedo     = _CompressedColor.rgb * 0.65;
                    smoothness = _IceSmoothness;
                    ao         = 0.5;
                }

                // ── URP InputData ─────────────────────────────────────────────
                InputData inputData = (InputData)0;
                inputData.positionWS              = IN.positionWS;
                inputData.normalWS                = N;
                inputData.viewDirectionWS         = normalize(IN.viewDirWS);
                inputData.shadowCoord             = IN.shadowCoord;
                inputData.fogCoord                = IN.fogFactor;
                inputData.vertexLighting           = half3(0, 0, 0);
                inputData.bakedGI                 = half3(0, 0, 0);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask              = SAMPLE_SHADOWMASK(float2(0, 0));

                // ── URP SurfaceData ───────────────────────────────────────────
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo       = albedo;
                surfaceData.metallic     = 0.0;
                surfaceData.specular     = half3(0.04, 0.04, 0.04);
                surfaceData.smoothness   = smoothness;
                surfaceData.normalTS     = half3(0, 0, 1);
                surfaceData.occlusion    = ao;
                surfaceData.emission     = half3(0, 0, 0);
                surfaceData.alpha        = 1.0;

                float4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb    = MixFog(color.rgb, IN.fogFactor);
                return color;
            }
            ENDHLSL
        }

        // ── Shadow Caster ─────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex   shadowVert
            #pragma hull     shadowHull
            #pragma domain   shadowDomain
            #pragma fragment shadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_TrailTex); SAMPLER(sampler_TrailTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _TrailTex_ST; float4 _SnowTex_ST;
                float4 _SnowColor; float4 _CompressedColor;
                float  _SnowSmoothness; float _IceSmoothness;
                float  _SnowTiling; float _SnowTexStrength;
                float  _SnowHeight; float _BermHeight; float _BermWidth;
                float  _NormalStrength;
                float  _TessFactor; float _TessMaxDistance;
            CBUFFER_END

            struct Attributes   { float4 pos:POSITION; float3 nor:NORMAL; float2 uv:TEXCOORD0; };
            struct CP           { float4 pos:INTERNALTESSPOS; float3 nor:NORMAL; float2 uv:TEXCOORD0; };
            struct TF           { float edge[3]:SV_TessFactor; float inside:SV_InsideTessFactor; };
            struct Varyings     { float4 cs:SV_POSITION; };

            CP shadowVert(Attributes i){CP o;o.pos=i.pos;o.nor=i.nor;o.uv=i.uv;return o;}
            TF ShadowPC(InputPatch<CP,3> p){
                float3 c=TransformObjectToWorld((p[0].pos+p[1].pos+p[2].pos).xyz/3);
                float f=max(1,_TessFactor*(1-saturate(distance(c,_WorldSpaceCameraPos)/_TessMaxDistance)));
                TF o;o.edge[0]=o.edge[1]=o.edge[2]=o.inside=f;return o;
            }
            [domain("tri")][outputcontrolpoints(3)][outputtopology("triangle_cw")]
            [partitioning("fractional_odd")][patchconstantfunc("ShadowPC")]
            CP shadowHull(InputPatch<CP,3> p,uint id:SV_OutputControlPointID){return p[id];}

            [domain("tri")]
            Varyings shadowDomain(TF tf,OutputPatch<CP,3> p,float3 b:SV_DomainLocation){
                float4 pos=p[0].pos*b.x+p[1].pos*b.y+p[2].pos*b.z;
                float3 nor=p[0].nor*b.x+p[1].nor*b.y+p[2].nor*b.z;
                float2 uv=p[0].uv*b.x+p[1].uv*b.y+p[2].uv*b.z;
                float t=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv,0).r;
                float groove=smoothstep(0.0,0.5,t);
                float2 offU=float2(_BermWidth,0); float2 offV=float2(0,_BermWidth);
                float tL=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv-offU,0).r;
                float tR=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv+offU,0).r;
                float tD=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv-offV,0).r;
                float tU=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv+offV,0).r;
                float berm=saturate(max(max(tL,tR),max(tD,tU))-t)*(1-t);
                berm=smoothstep(0,1,berm);
                float disp=-groove*_SnowHeight+berm*_BermHeight;
                float3 n=normalize(nor);
                pos.xyz+=n*disp;
                float3 wp=TransformObjectToWorld(pos.xyz);
                float3 wn=TransformObjectToWorldNormal(nor);
                float3 ld=normalize(_MainLightPosition.xyz);
                wp+=wn*(1-saturate(dot(wn,ld)))*0.02;
                Varyings o;o.cs=TransformWorldToHClip(wp);return o;
            }
            float4 shadowFrag(Varyings i):SV_Target{return 0;}
            ENDHLSL
        }

        // ── Depth Only ────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask 0

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex   depthVert
            #pragma hull     depthHull
            #pragma domain   depthDomain
            #pragma fragment depthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_TrailTex); SAMPLER(sampler_TrailTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _TrailTex_ST; float4 _SnowTex_ST;
                float4 _SnowColor; float4 _CompressedColor;
                float  _SnowSmoothness; float _IceSmoothness;
                float  _SnowTiling; float _SnowTexStrength;
                float  _SnowHeight; float _BermHeight; float _BermWidth;
                float  _NormalStrength;
                float  _TessFactor; float _TessMaxDistance;
            CBUFFER_END

            struct Attributes   { float4 pos:POSITION; float3 nor:NORMAL; float2 uv:TEXCOORD0; };
            struct CP           { float4 pos:INTERNALTESSPOS; float3 nor:NORMAL; float2 uv:TEXCOORD0; };
            struct TF           { float edge[3]:SV_TessFactor; float inside:SV_InsideTessFactor; };
            struct Varyings     { float4 cs:SV_POSITION; };

            CP depthVert(Attributes i){CP o;o.pos=i.pos;o.nor=i.nor;o.uv=i.uv;return o;}
            TF DepthPC(InputPatch<CP,3> p){
                float3 c=TransformObjectToWorld((p[0].pos+p[1].pos+p[2].pos).xyz/3);
                float f=max(1,_TessFactor*(1-saturate(distance(c,_WorldSpaceCameraPos)/_TessMaxDistance)));
                TF o;o.edge[0]=o.edge[1]=o.edge[2]=o.inside=f;return o;
            }
            [domain("tri")][outputcontrolpoints(3)][outputtopology("triangle_cw")]
            [partitioning("fractional_odd")][patchconstantfunc("DepthPC")]
            CP depthHull(InputPatch<CP,3> p,uint id:SV_OutputControlPointID){return p[id];}

            [domain("tri")]
            Varyings depthDomain(TF tf,OutputPatch<CP,3> p,float3 b:SV_DomainLocation){
                float4 pos=p[0].pos*b.x+p[1].pos*b.y+p[2].pos*b.z;
                float3 nor=p[0].nor*b.x+p[1].nor*b.y+p[2].nor*b.z;
                float2 uv=p[0].uv*b.x+p[1].uv*b.y+p[2].uv*b.z;
                float t=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv,0).r;
                float groove=smoothstep(0.0,0.5,t);
                float2 offU=float2(_BermWidth,0); float2 offV=float2(0,_BermWidth);
                float tL=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv-offU,0).r;
                float tR=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv+offU,0).r;
                float tD=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv-offV,0).r;
                float tU=SAMPLE_TEXTURE2D_LOD(_TrailTex,sampler_TrailTex,uv+offV,0).r;
                float berm=saturate(max(max(tL,tR),max(tD,tU))-t)*(1-t);
                berm=smoothstep(0,1,berm);
                float disp=-groove*_SnowHeight+berm*_BermHeight;
                float3 n=normalize(nor);
                pos.xyz+=n*disp;
                Varyings o;o.cs=TransformObjectToHClip(pos.xyz);return o;
            }
            float4 depthFrag(Varyings i):SV_Target{return 0;}
            ENDHLSL
        }
    }
}
