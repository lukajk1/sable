# SPDX-License-Identifier: GPL-3.0-or-later
"""Sable Link: live-link selected Blender meshes into Sable for texture painting.

"Send to Sable" exports the selected meshes to a link folder (model.glb + link.json) and starts Sable with
``--link <link.json>``. While linked, geometry/transform/material edits re-export after a short pause, and image
files Sable saves are reloaded in Blender.

"Stack Identical UV Islands" moves UV islands of the same shape onto one master island, so repeated parts share
one patch of texture.
"""

import os
import time

import bpy
from bpy.props import BoolProperty, FloatProperty, StringProperty

from . import link, uv_stack


class SABLE_AP_preferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    sable_path: StringProperty(
        name="Sable.exe",
        description="Path to Sable.exe. Leave empty to use the installed Sable",
        subtype='FILE_PATH',
        default="",
    )

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "sable_path")
        found = link.find_sable_exe(self.sable_path)
        layout.label(text="Using: " + found if found else "Sable.exe not found; set the path above",
                     icon='CHECKMARK' if found else 'ERROR')


def _prefs(context):
    addon = context.preferences.addons.get(__package__)
    return addon.preferences if addon else None


class SABLE_OT_send_selection(bpy.types.Operator):
    """Link the selected meshes to Sable: export them and start Sable if it isn't running"""
    bl_idname = "sable.send_selection"
    bl_label = "Send to Sable"
    bl_options = {'REGISTER'}  # No 'UNDO': linking never changes the scene.

    def execute(self, context):
        objects = [obj for obj in context.selected_objects if obj.type == 'MESH']
        if not objects:
            self.report({'ERROR'}, "Select one or more mesh objects to send to Sable")
            return {'CANCELLED'}

        need_launch = not link.sable_running()
        exe = None
        if need_launch:
            prefs = _prefs(context)
            exe = link.find_sable_exe(prefs.sable_path if prefs else "")
            if exe is None:
                self.report({'ERROR'}, "Sable.exe not found: set its path in the Sable Link add-on preferences")
                return {'CANCELLED'}

        error = link.start_link(objects)
        if error:
            self.report({'ERROR'}, error)
            return {'CANCELLED'}

        if need_launch:
            try:
                link.launch_sable(exe, link.link_json_path())
            except OSError as ex:
                self.report({'ERROR'}, "Could not start Sable ({}): {}".format(exe, ex))
                return {'CANCELLED'}
            self.report({'INFO'}, "Sent {} object(s) to Sable (starting Sable)".format(len(objects)))
        else:
            self.report({'INFO'}, "Sent {} object(s) to Sable".format(len(objects)))
        return {'FINISHED'}


class SABLE_OT_stop_link(bpy.types.Operator):
    """Stop sending updates to Sable (Sable keeps running)"""
    bl_idname = "sable.stop_link"
    bl_label = "Stop Sable Link"
    bl_options = {'REGISTER'}

    @classmethod
    def poll(cls, context):
        return link.state.active

    def execute(self, context):
        link.stop_link()
        self.report({'INFO'}, "Sable link stopped")
        return {'FINISHED'}


class SABLE_OT_stack_identical(bpy.types.Operator):
    """Move UV islands of the same shape exactly onto one master island, so identical parts share texture"""
    bl_idname = "uv.sable_stack_identical"
    bl_label = "Stack Identical UV Islands"
    bl_options = {'REGISTER', 'UNDO'}

    tolerance: FloatProperty(
        name="Tolerance",
        description="How far apart matching UV points may be, in UV units (1 = the texture's width)",
        default=0.002,
        min=0.0,
        soft_max=0.05,
        step=0.01,
        precision=4,
    )
    allow_mirror: BoolProperty(
        name="Allow Mirrored",
        description="Also stack islands that are mirror images of the master, flipping them onto it",
        default=True,
    )
    selected_only: BoolProperty(
        name="Selected Only",
        description="In Edit Mode, only islands with selected faces (selected UVs without UV sync selection) take part",
        default=False,
    )

    @classmethod
    def poll(cls, context):
        return context.mode in {'OBJECT', 'EDIT_MESH'} and bool(uv_stack.target_objects(context))

    def execute(self, context):
        result = uv_stack.stack_identical(context, tolerance=self.tolerance, allow_mirror=self.allow_mirror,
                                          selected_only=self.selected_only)
        if result.error:
            self.report({'ERROR'}, result.error)
            return {'CANCELLED'}
        if not result.groups:
            self.report({'WARNING'}, "No identical UV islands found ({} islands checked)".format(result.islands))
        elif not result.moved:
            self.report({'INFO'}, "{} group(s) of identical islands, already stacked".format(result.groups))
        else:
            self.report({'INFO'}, "Stacked {} island(s) onto {} master(s), {} mirrored ({:.0f} ms)".format(
                result.moved, result.groups, result.mirrored, result.seconds * 1000.0))
        return {'FINISHED'}


class SABLE_PT_panel(bpy.types.Panel):
    bl_label = "Sable Link"
    bl_idname = "SABLE_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Sable"

    def draw(self, context):
        layout = self.layout
        state = link.state
        layout.operator(SABLE_OT_send_selection.bl_idname, icon='EXPORT')

        col = layout.column(align=True)
        if state.active:
            objects = link.linked_objects()
            col.label(text="Linked: {} object(s)".format(len(objects)), icon='LINKED')
            for obj in objects[:8]:
                col.label(text="    " + obj.name)
            if len(objects) > 8:
                col.label(text="    ... and {} more".format(len(objects) - 8))
            if state.last_export_time is not None:
                col.label(text="Last export: {} ({:.0f} ms, rev {})".format(
                    time.strftime("%H:%M:%S", time.localtime(state.last_export_time)),
                    state.last_export_seconds * 1000.0, state.revision))
            if state.dirty:
                col.label(text="Update pending...")
        else:
            col.label(text="Not linked", icon='UNLINKED')
        if state.last_error:
            col.label(text=state.last_error, icon='ERROR')
        col.label(text="Sable is running" if link.sable_running() else "Sable not running (from here)",
                  icon='CHECKMARK' if link.sable_running() else 'BLANK1')
        if state.active and state.link_dir:
            col.label(text=os.path.basename(state.link_dir), icon='FILE_FOLDER')

        row = layout.row()
        row.enabled = state.active
        row.operator(SABLE_OT_stop_link.bl_idname, icon='CANCEL')


class SABLE_PT_uv_islands(bpy.types.Panel):
    bl_label = "UV Islands"
    bl_idname = "SABLE_PT_uv_islands"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Sable"
    bl_parent_id = SABLE_PT_panel.bl_idname

    def draw(self, context):
        layout = self.layout
        layout.operator(SABLE_OT_stack_identical.bl_idname, icon='UV_ISLANDSEL')
        col = layout.column(align=True)
        col.scale_y = 0.8
        col.label(text="Stacked copies share the master's texture:", icon='INFO')
        col.label(text="paint already on a copy is replaced by the master's.", icon='BLANK1')


def _object_menu(self, context):
    self.layout.separator()
    self.layout.operator(SABLE_OT_send_selection.bl_idname, icon='EXPORT')


def _uv_menu(self, context):
    self.layout.separator()
    self.layout.operator(SABLE_OT_stack_identical.bl_idname, icon='UV_ISLANDSEL')


_classes = (
    SABLE_AP_preferences,
    SABLE_OT_send_selection,
    SABLE_OT_stop_link,
    SABLE_OT_stack_identical,
    SABLE_PT_panel,
    SABLE_PT_uv_islands,
)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.VIEW3D_MT_object.append(_object_menu)
    bpy.types.IMAGE_MT_uvs.append(_uv_menu)
    link.register()


def unregister():
    link.unregister()
    bpy.types.IMAGE_MT_uvs.remove(_uv_menu)
    bpy.types.VIEW3D_MT_object.remove(_object_menu)
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
