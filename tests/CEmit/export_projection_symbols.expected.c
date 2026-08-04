// file: export_projection_symbols.c
#include "export_projection_symbols_private.h"

/* Private file declarations. */
void *malloc(uintptr_t size);
void free(void *ptr);

int ProjectionAbiBox_getValue(const ProjectionAbiBox *this)
{
	return ProjectionAbi_LIMIT;
}

int ProjectionAbiBox_getDefaultCapacity(void)
{
	return ProjectionAbi_LIMIT;
}

void ProjectionAbiBox_op_initnew(ProjectionAbiBox *this)
{
}

ProjectionAbiBox *ProjectionAbiBox_create(void)
{
	ProjectionAbiBox *_created0 = (ProjectionAbiBox *)(malloc(sizeof(ProjectionAbiBox)));
	if ((_created0 != NULL))
	{
		*_created0 = (ProjectionAbiBox){0};
		ProjectionAbiBox_op_initnew(_created0);
	}
	return _created0;
}

void ProjectionAbiBox_destroy(ProjectionAbiBox *this)
{
	free((void *)(this));
}

int ProjectionAbi_addOne(int value)
{
	return (value + 1);
}

void ProjectionAbiExportedBox_op_initnew(ProjectionAbiExportedBox *this)
{
}

ProjectionAbiExportedBox *ProjectionAbiExportedBox_create(void)
{
	ProjectionAbiExportedBox *_created1 = (ProjectionAbiExportedBox *)(malloc(sizeof(ProjectionAbiExportedBox)));
	if ((_created1 != NULL))
	{
		*_created1 = (ProjectionAbiExportedBox){0};
		ProjectionAbiExportedBox_op_initnew(_created1);
	}
	return _created1;
}

void ProjectionAbiExportedBox_destroy(ProjectionAbiExportedBox *this)
{
	free((void *)(this));
}

int ProjectionAbi_projected_add_one(int value)
{
	return ProjectionAbi_addOne(value);
}

// file: export_projection_symbols.h
#ifndef EXPORT_PROJECTION_SYMBOLS_H_
#define EXPORT_PROJECTION_SYMBOLS_H_

#include "export_projection_symbols_private.h"

int ProjectionAbiExportedBox_value(ProjectionAbiExportedBox *this);
int ProjectionAbiExportedBox_getDefaultCapacity(void);
void ProjectionAbiExportedBox_op_initnew(ProjectionAbiExportedBox *this);
ProjectionAbiExportedBox *ProjectionAbiExportedBox_create(void);
void ProjectionAbiExportedBox_destroy(ProjectionAbiExportedBox *this);
int ProjectionAbi_projected_add_one(int value);
#define ProjectionAbi_EXPORTED_LIMIT ((int)5)
#define ProjectionAbiExportedBox_LIMIT ((int)7)

#endif
// file: export_projection_symbols_private.h
#ifndef EXPORT_PROJECTION_SYMBOLS_PRIVATE_H_
#define EXPORT_PROJECTION_SYMBOLS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct ProjectionAbiBox ProjectionAbiBox;
typedef struct ProjectionAbiExportedBox ProjectionAbiExportedBox;

/* Enums. */

/* Newtypes. */

/* Constants. */
#define ProjectionAbi_LIMIT ((int)5)
#define ProjectionAbi_EXPORTED_LIMIT ((int)5)
#define ProjectionAbiBox_LIMIT ((int)7)
#define ProjectionAbiExportedBox_LIMIT ((int)7)

/* Layouts. */
struct ProjectionAbiBox
{
};
struct ProjectionAbiExportedBox
{
};

/* Function declarations. */
int ProjectionAbiBox_getValue(const ProjectionAbiBox *this);
int ProjectionAbiBox_getDefaultCapacity(void);
void ProjectionAbiBox_op_initnew(ProjectionAbiBox *this);
ProjectionAbiBox *ProjectionAbiBox_create(void);
void ProjectionAbiBox_destroy(ProjectionAbiBox *this);
int ProjectionAbi_addOne(int value);
int ProjectionAbiExportedBox_value(ProjectionAbiExportedBox *this);
int ProjectionAbiExportedBox_getDefaultCapacity(void);
void ProjectionAbiExportedBox_op_initnew(ProjectionAbiExportedBox *this);
ProjectionAbiExportedBox *ProjectionAbiExportedBox_create(void);
void ProjectionAbiExportedBox_destroy(ProjectionAbiExportedBox *this);
int ProjectionAbi_projected_add_one(int value);

/* Object declarations. */


#endif
