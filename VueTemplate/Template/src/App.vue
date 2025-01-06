<template>
	<div id="app">
		
	</div>
</template>

<script>
	
	import Navigation from "./components/Navigation.vue";
	
	import { createNamespacedHelpers } from "vuex";
	const { mapGetters, mapActions } = createNamespacedHelpers("module");


	export default {
		name: "app",
		data: function () {
			return {
				exampleData: 0
			}
		},
		props: {
			// type check
			exampleProp1: Number,
			// type check plus other validations
			exampleProp2: {
				type: Number,
				default: 0,
				required: false,
				validator: function (value) {
					return value >= 0
				}
			}
		},
		computed: {
			exampleComputed: function () {
				return 0;
			},
			exampleGetSet: {
				get: function () {
					return 0;
				},
				set: function (sel) {
					let val = sel;
				}
			},
			...mapGetters([
				
			])
		},
		methods: {
			exampleMethod: function () {
				return 0;
			},
			...mapActions([
				
			])
		},
		watch: {
			exampleData: function (newVal, oldVal) {

			}
		},
		filters: {
			capitalize: function (value) {
				if (!value) return ""
				value = value.toString()
				return value.charAt(0).toUpperCase() + value.slice(1)
			}//{{ message | capitalize }}
		},
		components: {
			Navigation,
		},
		beforeCreate: function () {
			
			
		},
		created: function () {


		},
		beforeMount: function () {


		},
		mounted: function () {


		},
		beforeUpdate: function () {


		},
		updated: function () {


		},
		activated: function () {


		},
		deactivated: function () {


		},
		beforeDestroy: function () {


		},
		
		destroyed: function () {
			
		},
		errorCaptured: function () {


		}
	};
</script>

<style scoped>
	
</style>
